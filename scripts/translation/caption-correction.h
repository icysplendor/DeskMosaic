#ifndef TRANSLATION_CAPTION_CORRECTION_H_
#define TRANSLATION_CAPTION_CORRECTION_H_

// One background decoder, with room for consecutive paragraph boundaries.
#include <errno.h>

#ifdef _WIN32
#define CorrectionPathSeparator "\\"
#else
#define CorrectionPathSeparator "/"
#endif

enum { CorrectionPathBytes = 4096 };
enum { CorrectionAudioSeconds = 36, CorrectionMaxSeconds = 30, CorrectionQueueSize = 2, CorrectionTextBytes = 8192 };
static const double CorrectionShortSeconds = 0.6;
typedef struct {
  float *samples;
  uint32_t rate;
  size_t total;
  double origin;
} CorrectionAudio;

typedef struct {
  unsigned id, source;
  int speaker, isolated;
  uint32_t rate;
  size_t count;
  CaptionRange range;
  double audio_start;
  char original[CorrectionTextBytes];
  float samples[];
} CorrectionJob;

enum CorrectionState { CorrectionWaitingForFiles, CorrectionLoading, CorrectionReady, CorrectionUnavailable };

#define ONLINE_CORRECTION_API(name) __typeof__(&SherpaOnnx##name) name
typedef struct {
  ONLINE_CORRECTION_API(CreateOnlineRecognizer);
  ONLINE_CORRECTION_API(DestroyOnlineRecognizer);
  ONLINE_CORRECTION_API(CreateOnlineStream);
  ONLINE_CORRECTION_API(DestroyOnlineStream);
  ONLINE_CORRECTION_API(OnlineStreamAcceptWaveform);
  ONLINE_CORRECTION_API(DecodeOnlineStream);
  ONLINE_CORRECTION_API(IsOnlineStreamReady);
  ONLINE_CORRECTION_API(OnlineStreamInputFinished);
  ONLINE_CORRECTION_API(GetOnlineStreamResult);
  ONLINE_CORRECTION_API(DestroyOnlineRecognizerResult);
  const char *directory;
  const char *program, *sherpa_path;
  int moss, live_turns;
  DiarMutex lock;
  DiarCondition event;
  DiarThread worker;
  enum CorrectionState state;
  int started, stopping, preparationReported;
  unsigned next_id;
  size_t head, queued;
  CorrectionJob *jobs[CorrectionQueueSize];
  CorrectionAudio audio[DiarSources];
} CaptionCorrection;
#undef ONLINE_CORRECTION_API

static int english_letter(unsigned char c) {
  return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

static int protected_char(unsigned char c) {
  return english_letter(c) || (c >= '0' && c <= '9');
}
static unsigned char folded_ascii(unsigned char c) { return c >= 'A' && c <= 'Z' ? c + 'a' - 'A' : c; }

static const char *protected_token(const char *text) {
  while (*text && !protected_char((unsigned char)*text)) ++text;
  return text;
}

static int preserves_words(const char *original, const char *revision) {
  const char *a = protected_token(original);
  const char *b = protected_token(revision);
  // A newly recovered English word can be accepted when the draft had none.
  // If the draft already contains a word or number, require the whole
  // sequence to stay intact, including order and token boundaries.
  if (!*a) return 1;
  while (*a && *b) {
    while (protected_char((unsigned char)*a) && protected_char((unsigned char)*b)) {
      if (folded_ascii((unsigned char)*a++) != folded_ascii((unsigned char)*b++)) return 0;
    }
    if (protected_char((unsigned char)*a) || protected_char((unsigned char)*b)) return 0;
    a = protected_token(a);
    b = protected_token(b);
  }
  return !*a && !*b;
}

static int chinese_caption(const char *text, size_t length) {
  for (size_t at = 0; at < length;) {
    size_t width;
    if (paragraph_cjk(paragraph_char(text + at, length - at, &width))) return 1;
    at += width;
  }
  return 0;
}

static int reviewable_text(const char *text, size_t length) {
  for (size_t i = 0; i < length; ++i)
    if (english_letter((unsigned char)text[i])) return 1;
  return chinese_caption(text, length);
}

static int accepts_english_revision(const char *original, const char *revision) {
  const char *a = protected_token(original);
  const char *b = protected_token(revision);
  size_t words = 0;
  int changed_word = 0;
  // Accept punctuation and case changes. In a longer caption, the two passes
  // may also disagree on one interior word. Protect numbers and both ends.
  while (*a && *b) {
    int equal = 1;
    int letters_only = 1;
    while (protected_char((unsigned char)*a) || protected_char((unsigned char)*b)) {
      int has_a = protected_char((unsigned char)*a);
      int has_b = protected_char((unsigned char)*b);
      if (!has_a || !has_b || folded_ascii((unsigned char)*a) != folded_ascii((unsigned char)*b))
        equal = 0;
      if ((has_a && !english_letter((unsigned char)*a)) || (has_b && !english_letter((unsigned char)*b)))
        letters_only = 0;
      a += has_a;
      b += has_b;
    }
    a = protected_token(a);
    b = protected_token(b);
    if (!equal) {
      if (changed_word || !letters_only || !words || !*a || !*b) return 0;
      changed_word = 1;
    }
    ++words;
  }
  return !*a && !*b && (!changed_word || words >= 5);
}

static int accepts_revision(const char *original, const char *revision) {
  int original_chinese = chinese_caption(original, strlen(original));
  int revision_chinese = chinese_caption(revision, strlen(revision));
  if (original_chinese != revision_chinese) return 0;
  return original_chinese ? preserves_words(original, revision)
                          : accepts_english_revision(original, revision);
}

static int preserves_edges(const char *original, const char *revision) {
  uint32_t first[2] = {0}, last[2] = {0};
  const char *texts[] = {original, revision};
  for (size_t i = 0; i < 2; ++i) {
    size_t length = strlen(texts[i]);
    for (size_t at = 0; at < length;) {
      size_t width;
      uint32_t code = paragraph_char(texts[i] + at, length - at, &width);
      at += width;
      if (!paragraph_cjk(code) && !(code < 128 && protected_char((unsigned char)code))) continue;
      if (code < 128) code = folded_ascii((unsigned char)code);
      if (!first[i]) first[i] = code;
      last[i] = code;
    }
  }
  return first[0] == first[1] && last[0] == last[1];
}

static void correction_diagnostic(const char *stage) {
  printf("D\t%s\n", stage);
  fflush(stdout);
}

// One line replaces the parent caption atomically. ASCII separators are
// reserved for the turn metadata and stripped from model-generated text.
enum { MossReplyBytes = 32768 };

static int32_t correction_decode_moss(MossReview *review, const CorrectionJob *job, char *payload) {
  if (!moss_review_decode(review, job->samples, (uint32_t)job->count, job->rate)) return -1;
  size_t used = 0;
  long long base = llround(job->audio_start * 1000);
  long long duration = llround(job->count * 1000.0 / job->rate);
  int turns = review->api.n_segments(review->session);
  int short_audio = job->count / (double)job->rate < CorrectionShortSeconds;
  if (short_audio && turns != 1) turns = 0;
  for (int i = 0; i < turns; ++i) {
    struct transcribe_segment segment;
    review->api.segment_init(&segment);
    if (review->api.get_segment(review->session, i, &segment) != TRANSCRIBE_OK ||
        !segment.text || !*segment.text) continue;
    // Very short audio can produce an unrelated word. Keep the streaming
    // result when the second pass cannot corroborate the short utterance.
    if (short_audio && (!accepts_revision(job->original, segment.text) ||
        !preserves_edges(job->original, segment.text))) continue;
    long long start = (long long)fmin(duration, fmax(0, segment.t0_ms));
    long long end = (long long)fmin(duration, fmax(start, segment.t1_ms));
    if (end <= start) continue;
    int header = snprintf(payload + used, MossReplyBytes - used, "%s%d\x1f%lld\x1f%lld\x1f",
                          used ? "\x1e" : "", segment.speaker_id, base + start, base + end);
    size_t length = strlen(segment.text);
    if (header < 0 || (size_t)header >= MossReplyBytes - used ||
        length >= MossReplyBytes - used - (size_t)header) {
      used = 0;
      break;
    }
    used += (size_t)header;
    for (size_t j = 0; j < length; ++j)
      payload[used++] = (unsigned char)segment.text[j] < 32 ? ' ' : segment.text[j];
  }
  return (int32_t)used;
}

static int moss_worker_main(NativeLibrary sherpa) {
  MossReview review = {0};
  const char *mode = getenv("ASSISTANT_MOSS_REVIEW");
  unsigned char ready = moss_review_open(&review, sherpa, mode && strcmp(mode, "cpu") == 0);
  if (fwrite(&ready, sizeof(ready), 1, stdout) != 1 || fflush(stdout) || !ready) return 3;

  CorrectionJob header;
  char payload[MossReplyBytes];
  while (fread(&header, sizeof(header), 1, stdin) == 1) {
    if (header.rate < 8000 || header.rate > 384000 || !header.count ||
        header.count > (size_t)header.rate * CorrectionMaxSeconds) break;
    CorrectionJob *job = malloc(sizeof(*job) + header.count * sizeof(float));
    if (!job) break;
    *job = header;
    int32_t size = -1;
    if (fread(job->samples, sizeof(float), job->count, stdin) == job->count)
      size = correction_decode_moss(&review, job, payload);
    free(job);
    if (fwrite(&size, sizeof(size), 1, stdout) != 1 ||
        (size > 0 && fwrite(payload, 1, (size_t)size, stdout) != (size_t)size) || fflush(stdout)) break;
  }
  review.api.session_free(review.session);
  return 0;
}

static int correction_publish_moss(MossProcess *worker, const CorrectionJob *job) {
  char payload[MossReplyBytes];
  int32_t size = -1;
  int connected = fwrite(job, sizeof(*job), 1, worker->input) == 1 &&
    fwrite(job->samples, sizeof(float), job->count, worker->input) == job->count && !fflush(worker->input) &&
    fread(&size, sizeof(size), 1, worker->output) == 1 && size <= MossReplyBytes &&
    (size <= 0 || fread(payload, 1, (size_t)size, worker->output) == (size_t)size);
  if (!connected || size < 0) correction_diagnostic("moss_inference_failed");
  size_t length = connected && size > 0 ? (size_t)size : 0;
  emit_caption(job->source, length ? 'G' : 'R', job->id, job->speaker, job->range, length ? payload : "", length);
  fflush(stdout);
  return connected;
}

static DIAR_WORKER correction_worker(void *context) {
  CaptionCorrection *correction = context;
#ifdef _WIN32
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
#elif defined(__APPLE__)
  pthread_set_qos_class_self_np(QOS_CLASS_UTILITY, 0);
#endif
  diar_lock(&correction->lock);
  while (correction->state == CorrectionWaitingForFiles && !correction->stopping)
    diar_wait(&correction->event, &correction->lock);
  int load = correction->state == CorrectionLoading;
  diar_unlock(&correction->lock);

  char encoder[CorrectionPathBytes], decoder[CorrectionPathBytes],
       joiner[CorrectionPathBytes], tokens[CorrectionPathBytes];
  const char *directory = correction->directory ? correction->directory : "";
  snprintf(encoder, sizeof(encoder), "%s" CorrectionPathSeparator "encoder.int8.onnx", directory);
  snprintf(decoder, sizeof(decoder), "%s" CorrectionPathSeparator "decoder.onnx", directory);
  snprintf(joiner, sizeof(joiner), "%s" CorrectionPathSeparator "joiner.int8.onnx", directory);
  snprintf(tokens, sizeof(tokens), "%s" CorrectionPathSeparator "tokens.txt", directory);
  SherpaOnnxOnlineRecognizerConfig config = {0};
  config.feat_config.sample_rate = 16000;
  config.feat_config.feature_dim = 80;
  config.model_config.transducer.encoder = encoder;
  config.model_config.transducer.decoder = decoder;
  config.model_config.transducer.joiner = joiner;
  config.model_config.tokens = tokens;
  config.model_config.num_threads = 1;
  config.model_config.provider = "cpu";
  config.decoding_method = "greedy_search";
  if (load) correction_diagnostic("engine_loading");
  MossProcess moss = {0};
  int moss_ready = load && correction->moss && moss_process_start(&moss, correction->program, correction->sherpa_path);
  const SherpaOnnxOnlineRecognizer *recognizer = load && !correction->moss ? correction->CreateOnlineRecognizer(&config) : NULL;
  int ready = recognizer || moss_ready;
  diar_lock(&correction->lock);
  correction->state = ready ? CorrectionReady : CorrectionUnavailable;
  if (load) correction_diagnostic(ready ? "engine_ready" : "engine_load_failed");
  for (;;) {
    while (ready && !correction->queued && !correction->stopping)
      diar_wait(&correction->event, &correction->lock);
    if (!correction->queued) break;
    CorrectionJob *job = correction->jobs[correction->head];
    correction->head = (correction->head + 1) % CorrectionQueueSize;
    --correction->queued;
    diar_unlock(&correction->lock);
    if (moss_ready) {
      int connected = correction_publish_moss(&moss, job);
      free(job);
      diar_lock(&correction->lock);
      if (!connected) {
        moss_ready = ready = 0;
        correction->state = CorrectionUnavailable;
      }
      continue;
    }
    const SherpaOnnxOnlineStream *stream = recognizer ? correction->CreateOnlineStream(recognizer) : NULL;
    const SherpaOnnxOnlineRecognizerResult *result = NULL;
    if (stream) {
      correction->OnlineStreamAcceptWaveform(stream, job->rate, job->samples, (int32_t)job->count);
      // Finish the wider stream with enough silence for one full block of
      // right context. The caption decoder never waits for this thread.
      static const float silence[8192] = {0};
      for (uint32_t padding = job->rate * 3; padding;) {
        uint32_t count = padding < 8192 ? padding : 8192;
        correction->OnlineStreamAcceptWaveform(stream, job->rate, silence, (int32_t)count);
        padding -= count;
      }
      correction->OnlineStreamInputFinished(stream);
      while (correction->IsOnlineStreamReady(recognizer, stream))
        correction->DecodeOnlineStream(recognizer, stream);
      result = correction->GetOnlineStreamResult(recognizer, stream);
    }
    const char *text = result && result->text ? result->text : "";
    // A slice cut during continuous speech can pick up a neighboring word.
    if (!*text || strpbrk(text, "\r\n\t") ||
        !accepts_revision(job->original, text) ||
        (!job->isolated && !preserves_edges(job->original, text))) text = "";
    emit_caption(job->source, stream ? 'R' : 'E', job->id, job->speaker, job->range, text, strlen(text));
    fflush(stdout);
    if (result) correction->DestroyOnlineRecognizerResult(result);
    if (stream) correction->DestroyOnlineStream(stream);
    free(job);
    diar_lock(&correction->lock);
  }
  diar_unlock(&correction->lock);
  if (recognizer) correction->DestroyOnlineRecognizer(recognizer);
  moss_process_close(&moss);
#ifdef _WIN32
  return 0;
#else
  return NULL;
#endif
}

static void correction_start(CaptionCorrection *correction) {
#ifdef _WIN32
  InitializeCriticalSection(&correction->lock);
  InitializeConditionVariable(&correction->event);
  correction->worker = CreateThread(NULL, 0, correction_worker, correction, 0, NULL);
  correction->started = correction->worker != NULL;
  if (!correction->started) DeleteCriticalSection(&correction->lock);
#else
  if (pthread_mutex_init(&correction->lock, NULL)) return;
  if (pthread_cond_init(&correction->event, NULL)) {
    pthread_mutex_destroy(&correction->lock);
    return;
  }
  correction->started = pthread_create(&correction->worker, NULL, correction_worker, correction) == 0;
  if (!correction->started) {
    pthread_cond_destroy(&correction->event);
    pthread_mutex_destroy(&correction->lock);
  }
#endif
}

static void correction_finish(CaptionCorrection *correction) {
  if (correction->started) {
    diar_lock(&correction->lock);
    correction->stopping = 1;
    diar_signal(&correction->event);
    diar_unlock(&correction->lock);
#ifdef _WIN32
    WaitForSingleObject(correction->worker, INFINITE);
    CloseHandle(correction->worker);
    DeleteCriticalSection(&correction->lock);
#else
    pthread_join(correction->worker, NULL);
    pthread_cond_destroy(&correction->event);
    pthread_mutex_destroy(&correction->lock);
#endif
  }
  for (size_t i = 0; i < DiarSources; ++i) free(correction->audio[i].samples);
}

static void correction_audio(CaptionCorrection *correction, uint32_t source, uint32_t rate,
                             const float *samples, uint32_t count, double packet_start) {
  if (!correction->started) return;
  CorrectionAudio *audio = &correction->audio[source];
  size_t capacity = (size_t)rate * CorrectionAudioSeconds;
  if (audio->rate != rate) {
    free(audio->samples);
    *audio = (CorrectionAudio){.samples = malloc(capacity * sizeof(float)), .rate = rate, .origin = packet_start};
  }
  if (!audio->samples) return;
  size_t index = audio->total % capacity;
  size_t first = count < capacity - index ? count : capacity - index;
  memcpy(audio->samples + index, samples, first * sizeof(float));
  memcpy(audio->samples, samples + first, (count - first) * sizeof(float));
  audio->total += count;
}

static int correction_files_ready(const CaptionCorrection *correction, int report) {
  const char *files[] = {"encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx", "tokens.txt"};
  for (size_t i = 0; i < sizeof(files) / sizeof(files[0]); ++i) {
    char path[CorrectionPathBytes];
    int length = snprintf(path, sizeof(path), "%s" CorrectionPathSeparator "%s", correction->directory, files[i]);
    int error = 0;
    FILE *file = NULL;
    if (length < 0 || (size_t)length >= sizeof(path)) error = ENAMETOOLONG;
#ifdef _WIN32
    else {
      wchar_t wide[CorrectionPathBytes];
      if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide, CorrectionPathBytes)) {
        file = _wfopen(wide, L"rb");
        if (!file) error = errno;
      } else {
        error = EINVAL;
      }
    }
#else
    else {
      file = fopen(path, "rb");
      if (!file) error = errno;
    }
#endif
    if (!file) {
      if (report) {
        char stage[96];
        snprintf(stage, sizeof(stage), "engine_file_open_failed file=%s errno=%d", files[i], error);
        correction_diagnostic(stage);
      }
      return 0;
    }
    fclose(file);
  }
  return 1;
}

static void correction_report_preparation(CaptionCorrection *correction, int ready) {
  if (!correction->started || correction->preparationReported) return;
  correction->preparationReported = 1;
  diar_lock(&correction->lock);
  correction->state = ready ? CorrectionLoading : CorrectionUnavailable;
  correction_diagnostic(ready ? "engine_files_readable" : "engine_files_unavailable");
  diar_signal(&correction->event);
  diar_unlock(&correction->lock);
}

// S events identify why a finalized caption could not enter the review queue.
enum CorrectionSkip {
  CorrectionQueued = 0, CorrectionModelFailed = 2, CorrectionNoReviewableText,
  CorrectionAudioUnaligned, CorrectionOutsideLimits, CorrectionQueueFull
};

static enum CorrectionSkip correction_offer(CaptionCorrection *correction, uint32_t source, int speaker,
                            const char *text, size_t length, CaptionRange range, CaptionRange audio_range, int isolated) {
  double begin = audio_range.start, end = audio_range.end;
  if (!correction->started) return CorrectionModelFailed;
  if (!correction->moss && !reviewable_text(text, length)) return CorrectionNoReviewableText;
  if (end <= begin) return CorrectionAudioUnaligned;
  if (length >= CorrectionTextBytes || end - begin > CorrectionMaxSeconds || (!correction->moss && end - begin < CorrectionShortSeconds))
    return CorrectionOutsideLimits;
  const CorrectionAudio *audio = &correction->audio[source];
  if (!audio->samples || begin < audio->origin) return CorrectionAudioUnaligned;
  size_t offset = (size_t)((begin - audio->origin) * audio->rate);
  size_t tail = (size_t)((end - audio->origin) * audio->rate);
  if (tail > audio->total) tail = audio->total;
  size_t capacity = (size_t)audio->rate * CorrectionAudioSeconds;
  if (offset >= tail || audio->total - offset > capacity) return CorrectionAudioUnaligned;

  diar_lock(&correction->lock);
  if (correction->state == CorrectionUnavailable || correction->queued == CorrectionQueueSize) {
    enum CorrectionSkip reason = correction->state == CorrectionUnavailable ? CorrectionModelFailed : CorrectionQueueFull;
    diar_unlock(&correction->lock);
    return reason;
  }
  size_t count = tail - offset;
  CorrectionJob *job = malloc(sizeof(*job) + count * sizeof(float));
  if (!job) { diar_unlock(&correction->lock); return CorrectionAudioUnaligned; }
  size_t index = offset % capacity;
  size_t first = count < capacity - index ? count : capacity - index;
  memcpy(job->samples, audio->samples + index, first * sizeof(float));
  memcpy(job->samples + first, audio->samples, (count - first) * sizeof(float));
  job->id = ++correction->next_id;
  job->source = source;
  job->speaker = speaker;
  job->isolated = isolated;
  job->count = count;
  job->rate = audio->rate;
  job->range = range;
  job->audio_start = audio->origin + offset / (double)audio->rate;
  memcpy(job->original, text, length);
  job->original[length] = 0;
  correction->jobs[(correction->head + correction->queued) % CorrectionQueueSize] = job;
  ++correction->queued;
  emit_caption(source, 'F', job->id, speaker, range, text, length);
  fflush(stdout);
  diar_signal(&correction->event);
  diar_unlock(&correction->lock);
  return CorrectionQueued;
}

#endif
