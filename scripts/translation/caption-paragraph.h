#ifndef TRANSLATION_CAPTION_PARAGRAPH_H_
#define TRANSLATION_CAPTION_PARAGRAPH_H_

#include <ctype.h>
#include <stdint.h>
#include <string.h>

typedef struct {
  size_t published;
  char previous[8192];
} CaptionParagraph;

static uint32_t paragraph_char(const char *text, size_t remaining, size_t *width) {
  const unsigned char *bytes = (const unsigned char *)text;
  uint32_t first = bytes[0];
  *width = 1;
  if (first < 0xc0 || remaining < 2) return first;
  size_t length = first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
  if (remaining < length) return first;
  uint32_t value = first & (0x7f >> length);
  for (size_t i = 1; i < length; ++i) {
    if ((bytes[i] & 0xc0) != 0x80) return first;
    value = (value << 6) | (bytes[i] & 0x3f);
  }
  *width = length;
  return value;
}

static int paragraph_cjk(uint32_t code) {
  return (code >= 0x3040 && code <= 0x30ff) || (code >= 0x3400 && code <= 0x9fff) ||
         (code >= 0xac00 && code <= 0xd7af) || (code >= 0x20000 && code <= 0x323af);
}

static int paragraph_sentence_end(const char *text, size_t index, uint32_t code) {
  if (code == '!' || code == '?' || code == 0x3002 || code == 0xff01 || code == 0xff1f) return 1;
  if (code != '.') return 0;

  unsigned char after = (unsigned char)text[index + 1];
  unsigned char before = index ? (unsigned char)text[index - 1] : 0;
  if (!index || before == '.' || after == '.' || isalnum(after)) return 0;

  size_t start = index;
  while (start > 0 && isalpha((unsigned char)text[start - 1])) --start;
  size_t length = index - start;
  if (length == 1 && isupper((unsigned char)text[start])) return 0;
  if (length <= 2 && start > 1 && text[start - 1] == '.' && isalpha((unsigned char)text[start - 2])) return 0;
  if (length > 0 && length < 5) {
    char word[5] = {0};
    for (size_t i = 0; i < length; ++i) word[i] = (char)tolower((unsigned char)text[start + i]);
    const char *abbreviations[] = {"mr", "mrs", "ms", "dr", "prof", "vs", "etc", "sr", "jr", "st"};
    for (size_t i = 0; i < sizeof(abbreviations) / sizeof(abbreviations[0]); ++i)
      if (strcmp(word, abbreviations[i]) == 0) return 0;
  }
  return 1;
}

static size_t paragraph_boundary(const char *text, size_t stable) {
  enum { PreferSentenceAfter = 128, AllowClauseAfter = 232, ReadingLimit = 320 };
  size_t units = 0, clause = 0, word = 0, index = 0;
  while (index < stable) {
    size_t width;
    uint32_t code = paragraph_char(text + index, stable - index, &width);
    units += paragraph_cjk(code) ? 2 : 1;
    size_t end = index + width;
    if (units >= PreferSentenceAfter && paragraph_sentence_end(text, index, code)) {
      // Keep closing quotes and brackets with the sentence they close.
      while (end < stable) {
        size_t closing_width;
        uint32_t closing = paragraph_char(text + end, stable - end, &closing_width);
        if (closing != '"' && closing != '\'' && closing != ')' && closing != ']' &&
            closing != 0x2019 && closing != 0x201d && closing != 0x00bb &&
            closing != 0x300d && closing != 0x300f && closing != 0xff09)
          break;
        end += closing_width;
      }
      return end;
    }
    if (units >= PreferSentenceAfter) {
      if (code == ' ' || paragraph_cjk(code)) word = end;
      if (code == ',' || code == ';' || code == ':' ||
          code == 0xff0c || code == 0xff1b || code == 0xff1a || code == 0x3001)
        clause = end;
    }
    if (units >= ReadingLimit) return clause ? clause : (word ? word : end);
    index = end;
  }
  return units >= AllowClauseAfter ? clause : 0;
}

static const char *caption_separator(const char *previous, size_t length, const char *next) {
  if (!length || !*next || previous[length - 1] == ' ' || *next == ' ') return "";
  size_t last = length - 1, width;
  while (last && ((unsigned char)previous[last] & 0xc0) == 0x80) --last;
  uint32_t left = paragraph_char(previous + last, length - last, &width);
  uint32_t right = paragraph_char(next, strlen(next), &width);
  return paragraph_cjk(left) || paragraph_cjk(right) || right >= 0x3000 ? "" : " ";
}

static size_t paragraph_stable(CaptionParagraph *state, const char *text, int endpoint) {
  size_t stable = 0;
  while (text[stable] && text[stable] == state->previous[stable]) ++stable;
  size_t length = strlen(text);
  size_t remembered = length < sizeof(state->previous) - 1 ? length : sizeof(state->previous) - 1;
  memcpy(state->previous, text, remembered);
  state->previous[remembered] = 0;
  return endpoint ? length : stable;
}

#endif
