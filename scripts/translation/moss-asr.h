#ifndef TRANSLATION_MOSS_ASR_H_
#define TRANSLATION_MOSS_ASR_H_

#include "transcribe.h"

// MOSS labels speakers within one inference. The event's batch ID scopes
// those labels, while the time range remains relative to the whole session.
enum { MossRate = 16000, MossBlock = 320, MossPreroll = 3200,
       MossPause = 10400, MossSegmentMax = 8 * MossRate,
       MossMixWait = MossRate / 2, MossRing = 4 * MossRate + 4096 };

#define MOSS_API(name) __typeof__(&transcribe_##name) name
typedef struct {
  MOSS_API(init_backends_default);
  MOSS_API(open);
  MOSS_API(session_free);
  MOSS_API(model_load_params_init);
  MOSS_API(session_params_init);
  MOSS_API(run_params_init);
  MOSS_API(run);
  MOSS_API(n_segments);
  MOSS_API(segment_init);
  MOSS_API(get_segment);
} MossApi;
#undef MOSS_API

typedef struct {
  float ring[MossRing];
  uint64_t start, received;
  int started;
  uint32_t rate;
  const SherpaOnnxLinearResampler *resampler;
} MossInput;

typedef struct {
  MossApi api;
  struct transcribe_session *session;
  struct transcribe_run_params params;
  int sources, batch;
  uint64_t cursor;
  float audio[MossSegmentMax];
  uint32_t count, quiet;
  int speech;
  MossInput input[2];
} MossRecognition;

static NativeLibrary moss_library(const char *path) {
#ifdef _WIN32
  (void)path;
  wchar_t dll[32768];
  DWORD size = GetModuleFileNameW(NULL, dll, sizeof(dll) / sizeof(*dll));
  if (!size || size >= sizeof(dll) / sizeof(*dll)) return NULL;
  wchar_t *last = wcsrchr(dll, L'\\');
  const wchar_t suffix[] = L"moss\\transcribe.dll";
  if (!last || last + 1 - dll + (sizeof(suffix) / sizeof(*suffix)) > sizeof(dll) / sizeof(*dll)) return NULL;
  wcscpy(last + 1, suffix);
  return LoadLibraryExW(dll, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
#else
  return dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
}

static int moss_load_api(MossApi *api, NativeLibrary library) {
  if (!library) return 0;
#define MOSS_LOAD(name) do { \
  api->name = (__typeof__(api->name))SYMBOL(transcribe_##name); \
  if (!api->name) return 0; \
} while (0)
  MOSS_LOAD(init_backends_default);
  MOSS_LOAD(open);
  MOSS_LOAD(session_free);
  MOSS_LOAD(model_load_params_init);
  MOSS_LOAD(session_params_init);
  MOSS_LOAD(run_params_init);
  MOSS_LOAD(run);
  MOSS_LOAD(n_segments);
  MOSS_LOAD(segment_init);
  MOSS_LOAD(get_segment);
#undef MOSS_LOAD
  return api->init_backends_default() == TRANSCRIBE_OK;
}

static int moss_decode(MossRecognition *state) {
  if (!state->speech) return 1;
  if (state->api.run(state->session, state->audio, (int)state->count, &state->params) != TRANSCRIBE_OK)
    return 0;

  uint64_t start_ms = (state->cursor - state->count) / (MossRate / 1000);
  uint64_t duration_ms = state->count / (MossRate / 1000);
  int rows = state->api.n_segments(state->session);
  ++state->batch;
  for (int i = 0; i < rows; ++i) {
    struct transcribe_segment segment;
    state->api.segment_init(&segment);
    if (state->api.get_segment(state->session, i, &segment) != TRANSCRIBE_OK ||
        !segment.text || !*segment.text) continue;
    uint64_t from = segment.t0_ms < 0 ? 0 : (uint64_t)segment.t0_ms;
    uint64_t to = segment.t1_ms < 0 ? from : (uint64_t)segment.t1_ms;
    if (from > duration_ms) from = duration_ms;
    if (to > duration_ms) to = duration_ms;
    if (to < from) to = from;
    printf("%d\tM%d\t%d\t%llu\t%llu\t", state->sources == 2, state->batch,
           segment.speaker_id, (unsigned long long)(start_ms + from), (unsigned long long)(start_ms + to));
    for (const unsigned char *c = (const unsigned char *)segment.text; *c; ++c)
      putchar(*c < 32 ? ' ' : *c);
    putchar('\n');
  }
  fflush(stdout);
  state->speech = 0;
  state->count = state->quiet = 0;
  return 1;
}

static int moss_mix(MossRecognition *state, int finish) {
  uint64_t first = state->input[0].received, second = state->input[1].received;
  uint64_t high = first > second ? first : second, low = first < second ? first : second;
  uint64_t limit = high;
  if (state->sources == 3 && !finish)
    limit = high - low > MossMixWait ? high - MossMixWait : low;
  while (limit > state->cursor && (finish || limit - state->cursor >= MossBlock)) {
    uint32_t block = limit - state->cursor < MossBlock ? (uint32_t)(limit - state->cursor) : MossBlock;
    float samples[MossBlock];
    for (uint32_t i = 0; i < block; ++i) {
      uint64_t at = state->cursor + i;
      float sum = 0;
      unsigned contributors = 0;
      for (unsigned source = 0; source < 2; ++source) {
        if (!(state->sources & (1 << source)) || at < state->input[source].start ||
            at >= state->input[source].received) continue;
        sum += state->input[source].ring[at % MossRing];
        ++contributors;
      }
      samples[i] = contributors == 2 ? 0.5f * sum : sum;
    }
    int speech = has_input(samples, block);
    if (!state->speech && !speech && state->count + block > MossPreroll) {
      uint32_t remove = state->count + block - MossPreroll;
      memmove(state->audio, state->audio + remove, (state->count - remove) * sizeof(float));
      state->count -= remove;
    }
    memcpy(state->audio + state->count, samples, block * sizeof(float));
    state->cursor += block;
    state->count += block;
    state->speech |= speech;
    state->quiet = speech ? 0 : state->quiet + block;
    if (state->speech && (state->quiet >= MossPause || state->count >= MossSegmentMax - MossBlock)) {
      if (!moss_decode(state)) return 0;
    }
  }
  return !finish || moss_decode(state);
}

static int moss_packet(MossRecognition *state, MicrophoneVad *resampling, unsigned source,
                       uint32_t rate, const float *samples, uint32_t count) {
  MossInput *input = &state->input[source];
  input->started = 1;
  if (input->rate != rate) {
    if (input->resampler) resampling->DestroyLinearResampler(input->resampler);
    input->resampler = rate == MossRate ? NULL : resampling->CreateLinearResampler(rate, MossRate, 0, 0);
    input->rate = rate;
  }
  const SherpaOnnxResampleOut *normalized = NULL;
  if (rate != MossRate) {
    if (!input->resampler) return 0;
    normalized = resampling->LinearResamplerResample(input->resampler, samples, (int32_t)count, 0);
    if (!normalized) return 0;
    samples = normalized->samples;
    count = (uint32_t)normalized->n;
  }
  int ok = 1;
  while (count) {
    uint32_t block = count < MossBlock ? count : MossBlock;
    for (uint32_t i = 0; i < block; ++i) {
      uint64_t at = input->received++;
      if (at >= state->cursor) input->ring[at % MossRing] = samples[i];
    }
    if (!moss_mix(state, 0)) { ok = 0; break; }
    samples += block;
    count -= block;
  }
  if (normalized) resampling->LinearResamplerResampleFree(normalized);
  return ok;
}

static int run_moss(const char *runtime, NativeLibrary sherpa, int use_cpu, int sources) {
  MossRecognition *state = calloc(1, sizeof(*state));
  if (!state) return 3;
  int status = 3;
  if (!moss_load_api(&state->api, moss_library(runtime))) {
    fprintf(stderr, "moss_runtime_unavailable\n");
    goto done;
  }
  MicrophoneVad resampling = load_microphone_vad(sherpa);
  if (!resampling.CreateLinearResampler || !resampling.DestroyLinearResampler ||
      !resampling.LinearResamplerResample || !resampling.LinearResamplerResampleFree) goto done;
  const char *path = getenv("ASSISTANT_MOSS_MODEL");
#ifdef _WIN32
  wchar_t wide[32768];
  char utf8[4 * 32768];
  DWORD length = GetEnvironmentVariableW(L"ASSISTANT_MOSS_MODEL", wide, sizeof(wide) / sizeof(*wide));
  path = NULL;
  if (length > 0 && length < sizeof(wide) / sizeof(*wide) &&
      WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide, -1, utf8, sizeof(utf8), NULL, NULL)) path = utf8;
#endif
  if (!path || !*path || sources < 1 || sources > 3) goto done;
  struct transcribe_model_load_params model;
  struct transcribe_session_params session;
  state->api.model_load_params_init(&model);
  state->api.session_params_init(&session);
  state->api.run_params_init(&state->params);
  session.n_threads = 4;
  model.backend = use_cpu ? TRANSCRIBE_BACKEND_CPU : TRANSCRIBE_BACKEND_AUTO;
  state->params.diarize = TRANSCRIBE_DIARIZE_MODE_ON;
  state->params.timestamps = TRANSCRIBE_TIMESTAMPS_SEGMENT;
  if (state->api.open(path, &model, &session, &state->session) != TRANSCRIBE_OK) {
    model.backend = TRANSCRIBE_BACKEND_CPU;
    if (use_cpu || state->api.open(path, &model, &session, &state->session) != TRANSCRIBE_OK) {
      fprintf(stderr, "moss_model_load_failed\n");
      goto done;
    }
  }
  state->sources = sources;
  puts("ready");
  fflush(stdout);
  float samples[32768];
  uint32_t header[3];
  status = 0;
  while (fread(header, sizeof(*header), 3, stdin) == 3) {
    uint32_t source = header[0], rate = header[1], count = header[2];
    // Initial source offsets use the shared capture clock, including when loopback starts late.
    if (source == UINT32_MAX - 1 && sources == 3 && rate < 2 && !state->input[rate].started) {
      state->input[rate].start = state->input[rate].received = count;
      continue;
    }
    if (source > 1 || !(sources & (1 << source)) || rate < 8000 || rate > 384000 ||
        !count || count > sizeof(samples) / sizeof(*samples) ||
        fread(samples, sizeof(*samples), count, stdin) != count) {
      puts("A\tinvalid_audio_packet");
      status = 5;
      break;
    }
    if (!moss_packet(state, &resampling, source, rate, samples, count)) { status = 3; break; }
  }
  if (!status && !moss_mix(state, 1)) status = 3;
  for (unsigned i = 0; i < 2; ++i)
    if (state->input[i].resampler) resampling.DestroyLinearResampler(state->input[i].resampler);
  state->api.session_free(state->session);
  if (status == 3) fprintf(stderr, "moss_inference_failed\n");
done:
  free(state);
  return status;
}

#endif
