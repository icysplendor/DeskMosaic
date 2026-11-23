#ifndef TRANSLATION_ONLINE_DIARIZATION_H_
#define TRANSLATION_ONLINE_DIARIZATION_H_

#include "nemo_speech/diar.h"

#ifdef _WIN32
#include <wchar.h>
typedef CRITICAL_SECTION DiarMutex;
typedef CONDITION_VARIABLE DiarCondition;
typedef HANDLE DiarThread;
#define diar_lock EnterCriticalSection
#define diar_unlock LeaveCriticalSection
#define diar_signal WakeConditionVariable
#define diar_wait(cv, lock) SleepConditionVariableCS(cv, lock, INFINITE)
#define DIAR_WORKER DWORD WINAPI
#else
#include <pthread.h>
typedef pthread_mutex_t DiarMutex;
typedef pthread_cond_t DiarCondition;
typedef pthread_t DiarThread;
#define diar_lock pthread_mutex_lock
#define diar_unlock pthread_mutex_unlock
#define diar_signal pthread_cond_signal
#define diar_wait pthread_cond_wait
#define DIAR_WORKER void *
#endif

enum { DiarSources = 2, MaxDiarTurns = 128, MaxDiarSpeakers = 8, MaxDiarPackets = 1024 };

typedef struct {
  double start;
  int speaker;
} DiarTurn;

typedef struct {
  DiarTurn turns[MaxDiarTurns];
  size_t count;
  double covered;
} DiarTimeline;

typedef struct {
  double after, duration;
} DiarGap;

typedef struct {
  nemo_speech_diar_stream *stream;
  int64_t consumed;
  int speaker;
  int candidate;
  int64_t candidate_since;
  double candidate_start;
  double fed, skipped;
  DiarGap gap;
  int ids[MaxDiarSpeakers];
  float decimation_sum;
  uint32_t decimation_count;
} DiarInput;

typedef struct DiarPacket {
  struct DiarPacket *next;
  uint32_t source, rate, count;
  double gap;
  float samples[];
} DiarPacket;

#define DIAR_API(field, name) __typeof__(&nemo_speech_diar_##name) field

typedef struct {
  DIAR_API(create, create);
  DIAR_API(destroy, destroy);
  DIAR_API(open, stream_open);
  DIAR_API(push, stream_push_f32);
  DIAR_API(finish, stream_finish);
  DIAR_API(close, stream_close);
  DIAR_API(frames, frame_count);
  DIAR_API(first_frame, frame_probs_start);
  DIAR_API(probabilities, frame_probs);
  DIAR_API(speakers, num_speakers);
  DIAR_API(cadence, seconds_per_frame);
  nemo_speech_diar_model *model;
  DiarInput inputs[DiarSources];
  DiarTimeline timeline[DiarSources];
  float *scores;
  size_t score_capacity;
  double seconds_per_frame;
  int speaker_count, next_speaker;
  DiarMutex lock;
  DiarCondition event;
  DiarThread worker;
  DiarPacket *head, *tail;
  size_t queued;
  double queued_seconds;
  double quiet[DiarSources], skipped[DiarSources];
  int stopping, failed;
} Diarizer;

static int diar_library(Diarizer *diar, const char *path, int prefer_gpu) {
#ifdef _WIN32
  (void)path;
  wchar_t executable[32768];
  DWORD size = GetModuleFileNameW(NULL, executable, sizeof(executable) / sizeof(*executable));
  if (!size || size >= sizeof(executable) / sizeof(*executable) - 64) return 0;
  wchar_t *filename = wcsrchr(executable, L'\\');
  if (!filename) return 0;
  const DWORD search = LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32;
  HMODULE library = NULL;
  if (prefer_gpu) {
    wcscpy(filename + 1, L"diarization-vulkan\\nemo_speech_asr_c.dll");
    library = LoadLibraryExW(executable, NULL, search);
  }
  if (!library) {
    wcscpy(filename + 1, L"diarization-cpu\\nemo_speech_asr_c.dll");
    library = LoadLibraryExW(executable, NULL, search);
  }
  #define DIAR_SYMBOL(name) GetProcAddress(library, "nemo_speech_diar_" #name)
#else
  (void)prefer_gpu;
  void *library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
  #define DIAR_SYMBOL(name) dlsym(library, "nemo_speech_diar_" #name)
#endif
  if (!library) return 0;
  #define DIAR_LOAD(field, name) \
    diar->field = (__typeof__(diar->field))DIAR_SYMBOL(name); \
    if (!diar->field) return 0
  DIAR_LOAD(create, create);
  DIAR_LOAD(destroy, destroy);
  DIAR_LOAD(open, stream_open);
  DIAR_LOAD(push, stream_push_f32);
  DIAR_LOAD(finish, stream_finish);
  DIAR_LOAD(close, stream_close);
  DIAR_LOAD(frames, frame_count);
  DIAR_LOAD(first_frame, frame_probs_start);
  DIAR_LOAD(probabilities, frame_probs);
  DIAR_LOAD(speakers, num_speakers);
  DIAR_LOAD(cadence, seconds_per_frame);
  #undef DIAR_LOAD
  #undef DIAR_SYMBOL
  return 1;
}

static double diar_audio_time(DiarInput *input, double time) {
  if (input->gap.duration && time >= input->gap.after) {
    input->skipped += input->gap.duration;
    input->gap = (DiarGap){0};
  }
  return time + input->skipped;
}

static int diar_read_frames(Diarizer *diar, uint32_t source) {
  DiarInput *input = &diar->inputs[source];
  int64_t frames = diar->frames(input->stream);
  if (frames <= input->consumed) return 1;
  int64_t first = diar->first_frame(input->stream);
  size_t cells = (size_t)(frames - first) * diar->speaker_count;
  if (cells > diar->score_capacity) {
    size_t capacity = cells * 2;
    float *scores = realloc(diar->scores, capacity * sizeof(*scores));
    if (!scores) return 0;
    diar->scores = scores;
    diar->score_capacity = capacity;
  }
  if (diar->probabilities(input->stream, diar->scores, diar->score_capacity)) return 0;
  diar_lock(&diar->lock);
  DiarTimeline *timeline = &diar->timeline[source];
  for (int64_t frame = input->consumed > first ? input->consumed : first; frame < frames; ++frame) {
    const float *scores = diar->scores + (frame - first) * diar->speaker_count;
    double audio_time = diar_audio_time(input, frame * diar->seconds_per_frame);
    float confidence = 0.50f;
    int best = 0;
    for (int speaker = 0; speaker < diar->speaker_count; ++speaker) {
      if (scores[speaker] <= confidence) continue;
      confidence = scores[speaker];
      best = speaker + 1;
    }
    // Keep the current speaker through overlap unless the new one is clearly stronger.
    if (best && input->speaker && scores[input->speaker - 1] >= 0.50f &&
        confidence - scores[input->speaker - 1] < 0.15f) best = input->speaker;
    if (input->candidate != best) {
      input->candidate = best;
      input->candidate_since = frame;
      input->candidate_start = audio_time;
    }
    if (!best || best == input->speaker ||
        (frame - input->candidate_since + 1) * diar->seconds_per_frame < 0.20) continue;
    input->speaker = best;
    if (timeline->count == MaxDiarTurns) {
      memmove(timeline->turns, timeline->turns + 1, (MaxDiarTurns - 1) * sizeof(DiarTurn));
      --timeline->count;
    }
    if (!input->ids[best - 1]) input->ids[best - 1] = ++diar->next_speaker;
    timeline->turns[timeline->count++] = (DiarTurn){
      input->candidate_start, input->ids[best - 1]
    };
  }
  input->consumed = frames;
  timeline->covered = diar_audio_time(input, frames * diar->seconds_per_frame);
  diar_unlock(&diar->lock);
  return 1;
}

static DIAR_WORKER diar_work(void *context) {
  Diarizer *diar = context;
  for (;;) {
    diar_lock(&diar->lock);
    while (!diar->head && !diar->stopping && !diar->failed) diar_wait(&diar->event, &diar->lock);
    DiarPacket *packet = diar->head;
    if (!packet || diar->failed) {
      diar_unlock(&diar->lock);
      break;
    }
    diar->head = packet->next;
    if (!diar->head) diar->tail = NULL;
    --diar->queued;
    diar->queued_seconds -= packet->count / (double)packet->rate;
    diar_signal(&diar->event);
    diar_unlock(&diar->lock);
    DiarInput *input = &diar->inputs[packet->source];
    if (packet->gap) input->gap = (DiarGap){input->fed, packet->gap};
    // The capture backend can supply 192/384 kHz, above NeMo's 96 kHz limit.
    // Keep a partial group across packets so the speaker and ASR clocks agree.
    if (packet->rate > 96000) {
      uint32_t factor = (packet->rate + 95999) / 96000;
      uint32_t reduced = 0;
      for (uint32_t i = 0; i < packet->count; ++i) {
        input->decimation_sum += packet->samples[i];
        if (++input->decimation_count != factor) continue;
        packet->samples[reduced++] = input->decimation_sum / factor;
        input->decimation_sum = 0;
        input->decimation_count = 0;
      }
      packet->count = reduced;
      packet->rate /= factor;
    }
    input->fed += packet->count / (double)packet->rate;
    int success = (!packet->count || !diar->push(input->stream, packet->samples, packet->count, packet->rate)) &&
                  diar_read_frames(diar, packet->source);
    free(packet);
    if (!success) {
      diar_lock(&diar->lock);
      diar->failed = 1;
      diar_signal(&diar->event);
      diar_unlock(&diar->lock);
    }
  }
  for (int source = 0; source < DiarSources; ++source) {
    DiarInput *input = &diar->inputs[source];
    if (input->consumed && !diar->finish(input->stream)) diar_read_frames(diar, source);
  }
  return 0;
}

static int diar_start(Diarizer *diar, const char *path, int prefer_gpu) {
  nemo_speech_diar_model_config config = {
    .size = sizeof(config), .model_path = path, .gpu = prefer_gpu ? 0 : -1,
    .preset = "v3-streaming", .left_context_frames = -1
  };
  if (diar->create(&config, &diar->model)) {
    if (!prefer_gpu) return 0;
    config.gpu = -1;
    if (diar->create(&config, &diar->model)) return 0;
  }
  diar->seconds_per_frame = diar->cadence(diar->model);
  diar->speaker_count = diar->speakers(diar->model);
  if (diar->speaker_count <= 0 || diar->speaker_count > MaxDiarSpeakers ||
      !isfinite(diar->seconds_per_frame) || diar->seconds_per_frame <= 0) return 0;
  for (int i = 0; i < DiarSources; ++i)
    if (diar->open(diar->model, &diar->inputs[i].stream)) return 0;
#ifdef _WIN32
  InitializeCriticalSection(&diar->lock);
  InitializeConditionVariable(&diar->event);
  diar->worker = CreateThread(NULL, 0, diar_work, diar, 0, NULL);
  return diar->worker != NULL;
#else
  if (pthread_mutex_init(&diar->lock, NULL) || pthread_cond_init(&diar->event, NULL)) return 0;
  return pthread_create(&diar->worker, NULL, diar_work, diar) == 0;
#endif
}

static void diar_enqueue(Diarizer *diar, uint32_t source, uint32_t rate, uint32_t count,
                         const float *samples, int has_signal) {
  double duration = count / (double)rate;
  // Keep enough trailing context to score the last voice. Longer silence
  // needs no inference, but must still advance this input's audio clock.
  if (!has_signal && diar->quiet[source] >= 2.0) {
    diar->skipped[source] += duration;
    return;
  }
  diar->quiet[source] = has_signal ? 0 : diar->quiet[source] + duration;
  size_t bytes = count * sizeof(*samples);
  DiarPacket *packet = malloc(sizeof(*packet) + bytes);
  if (packet) {
    *packet = (DiarPacket){
      .source = source, .rate = rate, .count = count, .gap = diar->skipped[source]
    };
    diar->skipped[source] = 0;
    memcpy(packet->samples, samples, bytes);
  }
  diar_lock(&diar->lock);
  if (!packet) diar->failed = 1;
  // Startup replay can arrive faster than the speaker model can run. Let the
  // app's bounded audio queue absorb that burst through pipe backpressure.
  while (!diar->failed && (diar->queued == MaxDiarPackets || diar->queued_seconds >= 6.0))
    diar_wait(&diar->event, &diar->lock);
  if (diar->failed) {
    free(packet);
  } else {
    if (diar->tail) diar->tail->next = packet;
    else diar->head = packet;
    diar->tail = packet;
    ++diar->queued;
    diar->queued_seconds += duration;
  }
  diar_signal(&diar->event);
  diar_unlock(&diar->lock);
}

static DiarTimeline diar_snapshot(Diarizer *diar, uint32_t source) {
  diar_lock(&diar->lock);
  DiarTimeline timeline = diar->timeline[source];
  if (diar->failed) {
    // A stale voice must not be applied to later paragraphs if diarization falls behind.
    timeline = (DiarTimeline){.covered = INFINITY};
  }
  diar_unlock(&diar->lock);
  return timeline;
}

static void diar_join(Diarizer *diar) {
  diar_lock(&diar->lock);
  diar->stopping = 1;
  diar_signal(&diar->event);
  diar_unlock(&diar->lock);
#ifdef _WIN32
  WaitForSingleObject(diar->worker, INFINITE);
  CloseHandle(diar->worker);
#else
  pthread_join(diar->worker, NULL);
#endif
}

static void diar_destroy(Diarizer *diar) {
#ifdef _WIN32
  DeleteCriticalSection(&diar->lock);
#else
  pthread_cond_destroy(&diar->event);
  pthread_mutex_destroy(&diar->lock);
#endif
  while (diar->head) {
    DiarPacket *packet = diar->head;
    diar->head = packet->next;
    free(packet);
  }
  for (int i = 0; i < DiarSources; ++i) diar->close(diar->inputs[i].stream);
  diar->destroy(diar->model);
  free(diar->scores);
}

#endif
