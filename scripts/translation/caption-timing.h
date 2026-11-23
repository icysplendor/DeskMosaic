#ifndef TRANSLATION_CAPTION_TIMING_H_
#define TRANSLATION_CAPTION_TIMING_H_

static const double ZipformerTokenContext = 0.26;

typedef struct {
  double start, end;
} CaptionRange;

static void emit_caption(uint32_t source, char event, unsigned revision, int speaker,
                         CaptionRange range, const char *text, size_t length) {
  char kind[24] = {event};
  if (revision) snprintf(kind + 1, sizeof(kind) - 1, "%u", revision);
  long long start = llround(fmax(0, range.start) * 1000);
  long long end = llround(fmax(range.start, range.end) * 1000);
  printf("%u\t%s\t%d\t%lld\t%lld\t%.*s\n", source, kind, speaker, start, end, (int)length, text);
}

#endif
