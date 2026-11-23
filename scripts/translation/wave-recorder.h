#ifndef TRANSLATION_WAVE_RECORDER_H_
#define TRANSLATION_WAVE_RECORDER_H_

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { WaveRate = 48000, WaveHeaderSize = 80, WaveBatchBytes = 20 * 1024 * 1024 };

typedef struct {
  FILE *file;
  int16_t *buffer;
  int channels;
  size_t batch_frames;
  size_t capacity_frames;
  size_t used_frames;
  uint64_t written_frames;
  uint64_t start_ms;
  uint64_t first_packet_frames;
  uint64_t next_frame[2];
  double phase[2];
  float previous[2];
} WaveRecorder;

static int wave_open(WaveRecorder *wave) {
  if (wave->file) return 1;
#ifdef _WIN32
  const wchar_t *path = _wgetenv(L"ASSISTANT_RECORDING_PATH");
  wave->file = path ? _wfopen(path, L"wb") : NULL;
#else
  const char *path = getenv("ASSISTANT_RECORDING_PATH");
  wave->file = path ? fopen(path, "wb") : NULL;
#endif
  if (!wave->file) return 0;
  unsigned char reserved[WaveHeaderSize] = {0};
  return fwrite(reserved, 1, sizeof(reserved), wave->file) == sizeof(reserved);
}

static int wave_flush(WaveRecorder *wave, size_t frames) {
  if (!wave_open(wave) ||
      fwrite(wave->buffer, (size_t)wave->channels * sizeof(int16_t), frames, wave->file) != frames ||
      fflush(wave->file) != 0) return 0;
  size_t remaining = wave->used_frames > frames ? wave->used_frames - frames : 0;
  size_t stride = (size_t)wave->channels * sizeof(int16_t);
  memmove(wave->buffer, wave->buffer + frames * wave->channels, remaining * stride);
  memset(wave->buffer + remaining * wave->channels, 0, (wave->capacity_frames - remaining) * stride);
  wave->written_frames += frames;
  wave->used_frames = remaining;
  return 1;
}

static int wave_append(WaveRecorder *wave, uint32_t source, uint32_t rate,
                       const float *samples, uint32_t count, uint64_t packet_ms) {
  const unsigned channel = wave->channels == 2 ? source : 0;
  const double ratio = WaveRate / (double)rate;
  double phase = wave->phase[channel];
  float previous = wave->next_frame[channel] ? wave->previous[channel] : samples[0];
  size_t output_frames = (size_t)(phase + count * ratio);
  if (!wave->first_packet_frames) {
    wave->start_ms = packet_ms;
    wave->first_packet_frames = output_frames;
  }
  uint64_t elapsed_ms = packet_ms > wave->start_ms ? packet_ms - wave->start_ms : 0;
  uint64_t elapsed_frames = elapsed_ms * (WaveRate / 1000) + wave->first_packet_frames;
  uint64_t start = elapsed_frames > output_frames ? elapsed_frames - output_frames : 0;
  uint64_t cursor = wave->next_frame[channel];
  if (start <= cursor + WaveRate * 80 / 1000) start = cursor;

  uint64_t frame = start;
  for (uint32_t i = 0; i < count; ++i) {
    float current = isfinite(samples[i]) ? fmaxf(-1, fminf(1, samples[i])) : 0;
    phase += ratio;
    while (phase >= 1) {
      phase -= 1;
      if (frame >= wave->written_frames) {
        while (frame - wave->written_frames >= wave->capacity_frames) {
          if (!wave_flush(wave, wave->batch_frames)) return 0;
        }
        size_t index = (size_t)(frame - wave->written_frames);
        float value = previous + (current - previous) * (float)(1 - phase / ratio);
        value = fmaxf(-1, fminf(1, value));
        wave->buffer[index * wave->channels + channel] = (int16_t)(32767 * value);
        if (wave->used_frames < index + 1) wave->used_frames = index + 1;
      }
      ++frame;
    }
    previous = current;
  }
  wave->phase[channel] = phase;
  wave->previous[channel] = previous;
  wave->next_frame[channel] = frame;

  // Keep a short tail for the other input's callback before committing interleaved audio.
  size_t grace = wave->channels == 2 ? WaveRate / 4 : 0;
  return wave->used_frames < wave->batch_frames + grace || wave_flush(wave, wave->batch_frames);
}

static void wave_le(unsigned char *out, uint64_t value, size_t bytes) {
  for (size_t i = 0; i < bytes; ++i) out[i] = (unsigned char)(value >> (8 * i));
}

static int wave_finish(WaveRecorder *wave) {
  if (wave->used_frames && !wave_flush(wave, wave->used_frames)) return 0;
  if (!wave->file) return 1;
  unsigned char header[WaveHeaderSize] = {0};
  uint16_t stride = (uint16_t)(wave->channels * sizeof(int16_t));
  uint64_t bytes = wave->written_frames * stride;
  int rf64 = bytes + WaveHeaderSize - 8 > UINT32_MAX;
  memcpy(header, rf64 ? "RF64" : "RIFF", 4);
  wave_le(header + 4, rf64 ? UINT32_MAX : bytes + WaveHeaderSize - 8, 4);
  memcpy(header + 8, rf64 ? "WAVEds64" : "WAVEJUNK", 8);
  wave_le(header + 16, 28, 4);
  if (rf64) {
    wave_le(header + 20, bytes + WaveHeaderSize - 8, 8);
    wave_le(header + 28, bytes, 8);
    wave_le(header + 36, wave->written_frames, 8);
  }
  memcpy(header + 48, "fmt ", 4);
  wave_le(header + 52, 16, 4);
  wave_le(header + 56, 1, 2);
  wave_le(header + 58, wave->channels, 2);
  wave_le(header + 60, WaveRate, 4);
  wave_le(header + 64, WaveRate * stride, 4);
  wave_le(header + 68, stride, 2);
  wave_le(header + 70, 16, 2);
  memcpy(header + 72, "data", 4);
  wave_le(header + 76, rf64 ? UINT32_MAX : bytes, 4);
  return fseek(wave->file, 0, SEEK_SET) == 0 &&
         fwrite(header, 1, sizeof(header), wave->file) == sizeof(header);
}

static int run_wave_recorder(int channels) {
  WaveRecorder wave = {0};
  wave.channels = channels;
  wave.batch_frames = WaveBatchBytes / (channels * sizeof(int16_t));
  wave.capacity_frames = wave.batch_frames + WaveRate / 4 + 32768 * 6;
  wave.buffer = calloc(wave.capacity_frames * channels, sizeof(int16_t));
  if (!wave.buffer) return 5;

  int ok = 1;
  uint32_t header[3];
  uint64_t packet_ms;
  float samples[32768];
  size_t fields;
  while ((fields = fread(header, sizeof(uint32_t), 3, stdin)) != 0) {
    if (fields != 3) {
      ok = 0;
      break;
    }
    uint32_t source = header[0], rate = header[1], count = header[2];
    if (source > 1 || rate < 8000 || rate > 384000 || count == 0 ||
        count > sizeof(samples) / sizeof(samples[0]) ||
        fread(&packet_ms, sizeof(packet_ms), 1, stdin) != 1 ||
        fread(samples, sizeof(float), count, stdin) != count ||
        !wave_append(&wave, source, rate, samples, count, packet_ms)) {
      ok = 0;
      break;
    }
  }
  if (ok) ok = !ferror(stdin) && wave_finish(&wave);
  if (wave.file && fclose(wave.file) != 0) ok = 0;
  free(wave.buffer);
  return ok ? 0 : 5;
}

#endif
