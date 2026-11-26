#ifndef TRANSLATION_DIARIZED_CAPTION_H_
#define TRANSLATION_DIARIZED_CAPTION_H_

static const double CaptionTailTime = 0.55;
static const double ParagraphSilence = 3.0;

typedef struct {
  size_t start, end, aligned_end;
  double time, last_token;
  int phrase_start;
} CaptionWord;

enum { DiarizedTextBytes = 8192, DiarizedWords = 1024 };

typedef struct {
  CaptionParagraph acoustic;
  // Keep the end of the visible paragraph across recognizer resets, including
  // its timestamps, so later speaker evidence can move it into the next row.
  char text[DiarizedTextBytes];
  CaptionWord words[DiarizedWords];
  size_t word_count;
  int speaker;
  double speaker_onset, last_word_time;
  CaptionRange carried_range;
} DiarizedCaption;

static double recognition_offset(const SherpaOnnxOnlineRecognizerResult *result) {
  const char *field = result->json ? strstr(result->json, "\"start_time\"") : NULL;
  return field && (field = strchr(field, ':')) ? strtod(field + 1, NULL) : 0;
}

static double last_token_time(const SherpaOnnxOnlineRecognizerResult *result, double origin) {
  if (result->timestamps && result->count > 0)
    return origin + recognition_offset(result) + result->timestamps[result->count - 1];
  return origin;
}

static int token_can_start_caption(const char *text, size_t offset, size_t length) {
  size_t width;
  uint32_t code = paragraph_char(text + offset, length - offset, &width);
  if (paragraph_cjk(code)) return 1;
  int latin = code < 128 ? isalnum((unsigned char)code)
                         : code >= 0xc0 && code <= 0x024f && code != 0xd7 && code != 0xf7;
  return latin && (!offset || isspace((unsigned char)text[offset - 1]));
}

static size_t append_caption_words(CaptionWord *words, size_t used, size_t base, size_t skipped, size_t stable,
                                   const SherpaOnnxOnlineRecognizerResult *result, double origin) {
  if (!result->timestamps || !result->tokens_arr) return used;
  const char *text = result->text;
  double offset = origin + recognition_offset(result);
  size_t index = 0;
  int first = 1;
  for (int32_t i = 0; i < result->count && index < stable && used < DiarizedWords * 2; ++i) {
    const char *token = result->tokens_arr[i];
    while (isspace((unsigned char)*token)) ++token;
    if (!*token) continue;
    while (index < stable && isspace((unsigned char)text[index])) ++index;
    size_t width = strlen(token);
    // Keep the original display text if normalization prevents exact alignment.
    if (width > stable - index || memcmp(text + index, token, width)) break;
    double time = offset + result->timestamps[i];
    if (token_can_start_caption(text, index, stable)) {
      if (index >= skipped) {
        double previous = used ? words[used - 1].time : time;
        size_t start = base + index - skipped;
        words[used++] = (CaptionWord){start, start + width, start + width, time, time, first || time - previous >= 0.40};
      }
      first = 0;
    } else if (used && index >= skipped && words[used - 1].start >= base) {
      words[used - 1].last_token = time;
      words[used - 1].aligned_end = base + index - skipped + width;
    }
    index += width;
  }
  return used;
}

// Use the same aligned words that determine which text moves across a split.
static CaptionRange caption_range(const CaptionWord *words, size_t count, size_t begin, size_t end,
                                  CaptionRange fallback) {
  size_t first = 0;
  while (first < count && words[first].start < begin) ++first;
  size_t next = first;
  while (next < count && words[next].start < end) ++next;
  if (first == next) return fallback;

  double start = fmax(0, words[first].time - ZipformerTokenContext);
  double finish = words[next - 1].last_token + 0.10;
  if (next < count) finish = fmin(finish, words[next].time - ZipformerTokenContext);
  else if (finish + 0.45 < fallback.end) finish = fallback.end; // Preserve an unaligned trailing phrase.
  finish = fmin(finish, fallback.end);
  return finish >= start ? (CaptionRange){start, finish} : fallback;
}

static void finalize_caption(CaptionCorrection *correction, uint32_t source, int speaker,
                             const char *text, size_t begin, size_t end,
                             const CaptionWord *words, size_t count, CaptionRange fallback,
                             CaptionRange isolated_audio) {
  while (end > begin && isspace((unsigned char)text[end - 1])) --end;
  while (begin < end && isspace((unsigned char)text[begin])) ++begin;
  if (end == begin) return;
  CaptionRange range = caption_range(words, count, begin, end, fallback);
  if (correction->directory || correction->moss) {
    size_t first = 0;
    while (first < count && words[first].start < begin) ++first;
    size_t next = first;
    while (next < count && words[next].start < end) ++next;

    CaptionRange audio = isolated_audio;
    int isolated = audio.end > audio.start;
    // A split often shares audio with the next paragraph. Use only the
    // matched tokens here; the display range may extend over an unmapped tail.
    if (!isolated && first < next && words[first].start == begin &&
        words[next - 1].aligned_end == end) {
      audio.start = fmax(0, words[first].time - ZipformerTokenContext);
      audio.end = fmin(fallback.end + 0.20, words[next - 1].last_token + 0.10);
      if (next < count) audio.end = fmin(audio.end, words[next].time - ZipformerTokenContext);
    }
    enum CorrectionSkip skip = correction_offer(correction, source, speaker, text + begin, end - begin,
                                                 range, audio, isolated);
    if (skip != CorrectionQueued)
      emit_caption(source, 'S', skip, speaker, range, text + begin, end - begin);
    return;
  }
  emit_caption(source, '1', 0, speaker, range, text + begin, end - begin);
}

static void publish_diarized_caption(DiarizedCaption *caption, uint32_t source,
                                     const SherpaOnnxOnlineRecognizerResult *result, int endpoint,
                                     double origin, const DiarTimeline *timeline, int flush,
                                     CaptionCorrection *correction, CaptionRange isolated_audio, double signal_end) {
  const char *text = result ? result->text : "";
  size_t length = strlen(text);
  size_t stable = paragraph_stable(&caption->acoustic, text, endpoint);
  size_t already_published = caption->acoustic.published < length ? caption->acoustic.published : length;
  while (already_published < length && ((unsigned char)text[already_published] & 0xc0) == 0x80)
    ++already_published;
  size_t prefix = strlen(caption->text);
  const char *separator = caption_separator(caption->text, prefix, text + already_published);
  size_t base = prefix + strlen(separator);
  char joined[DiarizedTextBytes * 2];
  int written = snprintf(joined, sizeof(joined), "%s%s%s", caption->text, separator, text + already_published);
  if (written < 0) return;
  size_t total = written < (int)sizeof(joined) ? (size_t)written : sizeof(joined) - 1;
  CaptionWord words[DiarizedWords * 2];
  size_t count = caption->word_count;
  memcpy(words, caption->words, count * sizeof(*words));
  if (result && total > base)
    count = append_caption_words(words, count, base, already_published, already_published + total - base, result, origin);
  size_t scored_limit = stable > already_published ? base + stable - already_published : base;
  CaptionRange fallback = {prefix ? caption->carried_range.start : origin,
                           result ? signal_end : caption->carried_range.end};

  size_t start = 0, scored = 0, turn = 0;
  int speaker = caption->speaker;
  for (size_t i = 0; i < count; ++i) {
    double acoustic_time = words[i].time - ZipformerTokenContext;
    if (!flush && (words[i].end > scored_limit || acoustic_time > timeline->covered - CaptionTailTime)) break;
    scored = words[i].end;
    while (turn < timeline->count && timeline->turns[turn].start <= acoustic_time) ++turn;
    if (!turn) continue;
    const DiarTurn *change = &timeline->turns[turn - 1];
    if (change->start < caption->speaker_onset) continue;
    if (speaker && speaker != change->speaker) {
      size_t boundary = words[i].start;
      // A turn may be confirmed after the opening words. Move back only to a
      // nearby phrase onset supported by the ASR reset or a pause between words.
      for (size_t j = i; j > 0; --j) {
        CaptionWord earlier = words[j - 1];
        if (earlier.start < start || earlier.time < change->start - 0.15 ||
            words[i].time - earlier.time > 0.48) break;
        if (earlier.phrase_start) {
          boundary = earlier.start;
          break;
        }
      }
      finalize_caption(correction, source, speaker, joined, start, boundary, words, count, fallback, (CaptionRange){0});
      start = boundary;
    }
    speaker = change->speaker;
    caption->speaker_onset = change->start;
  }

  // Never cut a readable paragraph through a short, still-unresolved tail.
  size_t limit = flush ? total : scored;
  size_t cut;
  while (limit > start && (cut = paragraph_boundary(joined + start, limit - start)) != 0) {
    finalize_caption(correction, source, speaker, joined, start, start + cut, words, count, fallback, (CaptionRange){0});
    start += cut;
    while (start < total && joined[start] == ' ') ++start;
  }
  if (flush) {
    // Only use the whole speech interval when it belongs to this row.
    double onset_gap = count ? words[0].time - ZipformerTokenContext - isolated_audio.start : 1;
    if (start != 0 || onset_gap < -0.15 || onset_gap > 0.70 ||
        fallback.end + 0.40 < isolated_audio.end)
      isolated_audio = (CaptionRange){0};
    finalize_caption(correction, source, speaker, joined, start, total, words, count, fallback, isolated_audio);
    *caption = (DiarizedCaption){0};
    return;
  }

  if (endpoint) {
    if (total - start >= sizeof(caption->text)) {
      finalize_caption(correction, source, speaker, joined, start, total, words, count, fallback, (CaptionRange){0});
      *caption = (DiarizedCaption){0};
      return;
    }
    caption->carried_range = caption_range(words, count, start, total, fallback);
    memcpy(caption->text, joined + start, total - start + 1);
    caption->word_count = 0;
    for (size_t i = 0; i < count && caption->word_count < DiarizedWords; ++i) {
      if (words[i].start < start) continue;
      words[i].start -= start;
      words[i].end -= start;
      words[i].aligned_end -= start;
      caption->words[caption->word_count++] = words[i];
    }
    caption->last_word_time = caption->word_count ? caption->words[caption->word_count - 1].time : 0;
    caption->acoustic = (CaptionParagraph){0};
  } else if (start >= base && start > 0) {
    // Both the carried prefix and the emitted portion of the ASR draft have
    // now been finalized. Subsequent hypotheses only publish the remainder.
    caption->text[0] = 0;
    caption->word_count = 0;
    caption->acoustic.published = already_published + start - base;
  } else if (start > 0) {
    memmove(caption->text, caption->text + start, prefix - start + 1);
    size_t kept = 0;
    for (size_t i = 0; i < caption->word_count; ++i) {
      CaptionWord word = caption->words[i];
      if (word.start < start) continue;
      word.start -= start;
      word.end -= start;
      word.aligned_end -= start;
      caption->words[kept++] = word;
    }
    caption->word_count = kept;
  }
  caption->speaker = speaker;
  if (start < total) {
    size_t stable_end = scored_limit < total ? scored_limit : total;
    unsigned boundary = preview_sentence_boundary(joined + start, stable_end > start ? stable_end - start : 0);
    emit_caption(source, '0', boundary, speaker, caption_range(words, count, start, total, fallback),
                 joined + start, total - start);
  }
}
#endif
