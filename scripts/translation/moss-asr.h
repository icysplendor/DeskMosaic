#ifndef TRANSLATION_MOSS_ASR_H_
#define TRANSLATION_MOSS_ASR_H_

#include "transcribe.h"

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

typedef struct {
  MossApi api;
  struct transcribe_session *session;
  struct transcribe_run_params params;
  __typeof__(&SherpaOnnxCreateLinearResampler) create_resampler;
  __typeof__(&SherpaOnnxDestroyLinearResampler) destroy_resampler;
  __typeof__(&SherpaOnnxLinearResamplerResample) resample;
  __typeof__(&SherpaOnnxLinearResamplerResampleFree) free_resample;
} MossReview;

static int moss_review_open(MossReview *review, NativeLibrary library, int cpu) {
  const char *runtime = getenv("ASSISTANT_MOSS_RUNTIME");
#ifndef _WIN32
  if (!runtime || !*runtime) return 0;
#endif
  if (!moss_load_api(&review->api, moss_library(runtime))) return 0;
  review->create_resampler = (__typeof__(review->create_resampler))SYMBOL(SherpaOnnxCreateLinearResampler);
  review->destroy_resampler = (__typeof__(review->destroy_resampler))SYMBOL(SherpaOnnxDestroyLinearResampler);
  review->resample = (__typeof__(review->resample))SYMBOL(SherpaOnnxLinearResamplerResample);
  review->free_resample = (__typeof__(review->free_resample))SYMBOL(SherpaOnnxLinearResamplerResampleFree);
  if (!review->create_resampler || !review->destroy_resampler || !review->resample || !review->free_resample) return 0;

  const char *path = getenv("ASSISTANT_MOSS_MODEL");
#ifdef _WIN32
  wchar_t wide[32768];
  char utf8[4 * 32768];
  DWORD length = GetEnvironmentVariableW(L"ASSISTANT_MOSS_MODEL", wide, sizeof(wide) / sizeof(*wide));
  path = NULL;
  if (length > 0 && length < sizeof(wide) / sizeof(*wide) &&
      WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide, -1, utf8, sizeof(utf8), NULL, NULL)) path = utf8;
#endif
  if (!path || !*path) return 0;
  struct transcribe_model_load_params model;
  struct transcribe_session_params session;
  review->api.model_load_params_init(&model);
  review->api.session_params_init(&session);
  review->api.run_params_init(&review->params);
  session.n_threads = 2;
  model.backend = cpu ? TRANSCRIBE_BACKEND_CPU : TRANSCRIBE_BACKEND_AUTO;
  review->params.diarize = TRANSCRIBE_DIARIZE_MODE_ON;
  review->params.timestamps = TRANSCRIBE_TIMESTAMPS_SEGMENT;
  if (review->api.open(path, &model, &session, &review->session) == TRANSCRIBE_OK) return 1;
  model.backend = TRANSCRIBE_BACKEND_CPU;
  return !cpu && review->api.open(path, &model, &session, &review->session) == TRANSCRIBE_OK;
}

static int moss_review_decode(MossReview *review, const float *samples, uint32_t count, uint32_t rate) {
  if (rate == 16000) return review->api.run(review->session, samples, (int)count, &review->params) == TRANSCRIBE_OK;
  const SherpaOnnxLinearResampler *resampler = review->create_resampler(rate, 16000, 0, 0);
  if (!resampler) return 0;
  const SherpaOnnxResampleOut *output = review->resample(resampler, samples, (int)count, 1);
  int ok = output && output->n > 0 &&
    review->api.run(review->session, output->samples, output->n, &review->params) == TRANSCRIBE_OK;
  if (output) review->free_resample(output);
  review->destroy_resampler(resampler);
  return ok;
}

#endif
