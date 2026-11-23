/* The app sends mono float32 PCM as {source, sample rate, sample count} followed by samples.
 * Output lines are source, event, speaker ID, start/end milliseconds, and UTF-8 text, separated by tabs.
 * 0 = draft, 1 = final, F<id> = final with a pending revision, R<id> = revision
 * (empty text retains the original).
 * Pipes keep audio and recognition local to the parent process.
 */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "sherpa-onnx/c-api/c-api.h"

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
typedef HMODULE NativeLibrary;
#define SYMBOL(name) GetProcAddress(library, #name)
#else
#include <dlfcn.h>
typedef void *NativeLibrary;
#define SYMBOL(name) dlsym(library, #name)
#endif

#include "caption-paragraph.h"
#include "caption-timing.h"
#include "wave-recorder.h"
#include "online-diarization.h"
#include "caption-correction.h"
#include "diarized-caption.h"
#include "microphone-vad.h"

static void startup_diagnostic(const char *stage) {
  if (getenv("MACASSISTANT_TRANSLATION_DIAGNOSTICS")) fprintf(stderr, "asr_startup=%s\n", stage);
}

#define LOAD(name) \
  __typeof__(&SherpaOnnx##name) name = (__typeof__(&SherpaOnnx##name))SYMBOL(SherpaOnnx##name); \
  if (!name) { startup_diagnostic("missing_symbol_" #name); return 2; }

enum { AudioSources = 2 };

typedef struct {
  float *preroll;
  size_t count;
  size_t capacity;
  int active, input_detected;
  double quiet;
  double received, signal_start, last_signal;
  double stream_origin;
} AudioActivity;

static int has_input(const float *samples, uint32_t count) {
  double energy = 0;
  for (uint32_t i = 0; i < count; ++i) {
    float square = samples[i] * samples[i];
    if (square > 0.000064f) return 1; // Preserve even a short consonant above -42 dBFS.
    energy += square;
  }
  return energy > 0.000004 * count;
}

static int remember_quiet(AudioActivity *audio, const float *samples, size_t count, uint32_t rate) {
  size_t capacity = rate / 5;
  if (capacity != audio->capacity) {
    float *memory = realloc(audio->preroll, capacity * sizeof(float));
    if (!memory) return 0;
    audio->preroll = memory;
    audio->capacity = capacity;
    audio->count = 0;
  }
  if (count >= capacity) {
    samples += count - capacity;
    count = capacity;
    audio->count = 0;
  }
  size_t retained = audio->count < capacity - count ? audio->count : capacity - count;
  memmove(audio->preroll, audio->preroll + audio->count - retained, retained * sizeof(float));
  memcpy(audio->preroll + retained, samples, count * sizeof(float));
  audio->count = retained + count;
  return 1;
}

static MicrophoneVad load_microphone_vad(NativeLibrary library) {
  MicrophoneVad microphone_vad = {0};
#define VAD_LOAD(name) microphone_vad.name = (__typeof__(&SherpaOnnx##name))SYMBOL(SherpaOnnx##name)
  VAD_LOAD(CreateVoiceActivityDetector);
  VAD_LOAD(DestroyVoiceActivityDetector);
  VAD_LOAD(VoiceActivityDetectorAcceptWaveform);
  VAD_LOAD(VoiceActivityDetectorDetected);
  VAD_LOAD(VoiceActivityDetectorEmpty);
  VAD_LOAD(VoiceActivityDetectorPop);
  VAD_LOAD(VoiceActivityDetectorReset);
  VAD_LOAD(CreateLinearResampler);
  VAD_LOAD(DestroyLinearResampler);
  VAD_LOAD(LinearResamplerResample);
  VAD_LOAD(LinearResamplerResampleFree);
#undef VAD_LOAD
  return microphone_vad;
}

int main(int argc, char **argv) {
  if (argc == 2 && strcmp(argv[1], "--help") == 0)
    return puts("streaming-asr THREADS LIBRARY [speaker.gguf DIAR_LIBRARY cpu|auto] [--correction MODEL_DIRECTORY]") < 0;
  const char *correction_directory = NULL;
  if ((argc == 5 || argc == 8) && strcmp(argv[argc - 2], "--correction") == 0) {
    correction_directory = argv[argc - 1];
    argc -= 2;
  }
  int checking_vad = argc == 4 && strcmp(argv[1], "--check-vad") == 0;
  if (argc != 3 && argc != 6 && !checking_vad) return 2;
#ifdef _WIN32
  char correction_utf8[CorrectionPathBytes];
  if (correction_directory) {
    wchar_t wide[CorrectionPathBytes];
    DWORD length = GetEnvironmentVariableW(L"WINASSISTANT_REVIEW_MODEL_DIR", wide, CorrectionPathBytes);
    if (length > 0 && length < CorrectionPathBytes &&
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, correction_utf8, CorrectionPathBytes, NULL, NULL))
      correction_directory = correction_utf8;
  }
  _setmode(_fileno(stdin), _O_BINARY);
  _setmode(_fileno(stdout), _O_BINARY);
#endif
  if (argc == 3 && strcmp(argv[1], "--record") == 0) {
    int channels = atoi(argv[2]);
    return channels == 1 || channels == 2 ? run_wave_recorder(channels) : 2;
  }
  Diarizer diar = {0};
  if (strcmp(argv[1], "--check-diarization") == 0) return diar_library(&diar, argv[2], 0) ? 0 : 4;
  int online_speakers = argc == 6;
  int checking = strcmp(argv[1], "--check") == 0;
#ifdef _WIN32
  HMODULE library = LoadLibraryExW(L"sherpa-onnx-c-api.dll", NULL,
      LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
#else
  void *library = dlopen(argv[2], RTLD_NOW | RTLD_LOCAL);
#endif
  if (!library) {
    startup_diagnostic("speech_library_unavailable");
#ifndef _WIN32
    if (getenv("MACASSISTANT_TRANSLATION_DIAGNOSTICS")) {
      const char *error = dlerror();
      if (error) fprintf(stderr, "%s\n", error);
    }
#endif
    return 2;
  }
  startup_diagnostic("speech_library_loaded");
  if (checking_vad) {
    MicrophoneVad vad = load_microphone_vad(library);
    microphone_vad_start(&vad, argv[3], 0.35f);
    int loaded = vad.detector != NULL;
    microphone_vad_finish(&vad);
    return loaded ? 0 : 5;
  }
  LOAD(GetVersionStr);
  if (checking) return puts(GetVersionStr()) < 0;
  LOAD(CreateOnlineRecognizer);
  LOAD(DestroyOnlineRecognizer);
  LOAD(CreateOnlineStream);
  LOAD(DestroyOnlineStream);
  LOAD(OnlineStreamAcceptWaveform);
  LOAD(IsOnlineStreamReady);
  LOAD(DecodeOnlineStream);
  LOAD(GetOnlineStreamResult);
  LOAD(DestroyOnlineRecognizerResult);
  LOAD(OnlineStreamIsEndpoint);
  LOAD(OnlineStreamReset);

  LOAD(OnlineStreamInputFinished);

  CaptionCorrection correction = {
      .directory = correction_directory,
      .CreateOnlineRecognizer = CreateOnlineRecognizer,
      .DestroyOnlineRecognizer = DestroyOnlineRecognizer,
      .CreateOnlineStream = CreateOnlineStream,
      .DestroyOnlineStream = DestroyOnlineStream,
      .OnlineStreamAcceptWaveform = OnlineStreamAcceptWaveform,
      .DecodeOnlineStream = DecodeOnlineStream,
      .IsOnlineStreamReady = IsOnlineStreamReady,
      .OnlineStreamInputFinished = OnlineStreamInputFinished,
      .GetOnlineStreamResult = GetOnlineStreamResult,
      .DestroyOnlineRecognizerResult = DestroyOnlineRecognizerResult,
  };

  SherpaOnnxOnlineRecognizerConfig config = {0};
  config.feat_config.sample_rate = 16000;
  config.feat_config.feature_dim = 80;
  config.model_config.transducer.encoder = "encoder.int8.onnx";
  config.model_config.transducer.decoder = "decoder.onnx";
  config.model_config.transducer.joiner = "joiner.int8.onnx";
  config.model_config.tokens = "tokens.txt";
  config.model_config.num_threads = atoi(argv[1]);
  config.model_config.provider = "cpu";
  config.decoding_method = "greedy_search";
  config.enable_endpoint = 1;
  config.rule1_min_trailing_silence = 2.4f;
  config.rule2_min_trailing_silence = 1.40f;
  config.rule3_min_utterance_length = 30.0f;

  startup_diagnostic("recognizer_loading");
  const SherpaOnnxOnlineRecognizer *recognizer = CreateOnlineRecognizer(&config);
  if (!recognizer) { startup_diagnostic("recognizer_rejected"); return 3; }
  const SherpaOnnxOnlineStream *streams[] = {CreateOnlineStream(recognizer), CreateOnlineStream(recognizer)};
  if (!streams[0] || !streams[1]) { startup_diagnostic("stream_creation_failed"); return 3; }
  startup_diagnostic("recognizer_ready");
  AudioActivity activity[AudioSources] = {0};
  CaptionParagraph paragraphs[AudioSources] = {0};
  DiarizedCaption diarized_captions[AudioSources] = {0};
  const SherpaOnnxOnlineRecognizerResult *drafts[AudioSources] = {0};
  double scored_through[AudioSources] = {0};
  double next_correction_check[AudioSources] = {0};
  if (online_speakers) {
    int prefer_gpu = strcmp(argv[5], "cpu") != 0;
    startup_diagnostic("speaker_library_loading");
    if (!diar_library(&diar, argv[4], prefer_gpu)) { startup_diagnostic("speaker_library_unavailable"); return 4; }
    startup_diagnostic("speaker_model_loading");
    if (!diar_start(&diar, argv[3], prefer_gpu)) { startup_diagnostic("speaker_model_failed"); return 4; }
    startup_diagnostic("speaker_model_ready");
  }

  const char *mic_mode = getenv("ASSISTANT_MIC_VAD");
  const char *mic_pause = getenv("ASSISTANT_MIC_PAUSE");
  const char *vad_model = getenv("ASSISTANT_MIC_VAD_MODEL");
#ifdef _WIN32
  char vad_model_utf8[CorrectionPathBytes];
  if (mic_mode && *mic_mode) {
    wchar_t wide[CorrectionPathBytes];
    DWORD length = GetEnvironmentVariableW(L"ASSISTANT_MIC_VAD_MODEL", wide, CorrectionPathBytes);
    vad_model = NULL;
    if (length > 0 && length < CorrectionPathBytes &&
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, vad_model_utf8, CorrectionPathBytes, NULL, NULL))
      vad_model = vad_model_utf8;
  }
#endif
  MicrophoneVad microphone_vad = {0};
  if (mic_mode && (strcmp(mic_mode, "gentle") == 0 || strcmp(mic_mode, "balanced") == 0)) {
    microphone_vad = load_microphone_vad(library);
    microphone_vad_start(&microphone_vad, vad_model, strcmp(mic_mode, "balanced") == 0 ? 0.50f : 0.35f);
  }
  double microphone_pause = mic_pause && strcmp(mic_pause, "long") == 0 ? 2.3 : 1.6;

  if (correction.directory) correction_start(&correction);
  setvbuf(stdout, NULL, _IOLBF, 0);
  puts("ready");
  fflush(stdout);
  if (mic_mode && strcmp(mic_mode, "off") != 0)
    correction_diagnostic(microphone_vad.detector ? "microphone_vad_ready" : "microphone_vad_unavailable");
  if (correction.directory) correction_diagnostic(correction.started ? "engine_waiting_for_files" : "engine_thread_failed");

  uint32_t header[3];
  float samples[32768];
  static const float tail_padding[8192] = {0};
  int status = 0;
  while (fread(header, sizeof(uint32_t), 3, stdin) == 3) {
    uint32_t source = header[0], rate = header[1], count = header[2];
    // This marker follows every packet already queued when recognition became ready.
    if (source == UINT32_MAX && rate == 2 && count == 0) {
      printf("A\tstartup_replayed system_ms=%.0f microphone_ms=%.0f\n",
             activity[0].received * 1000, activity[1].received * 1000);
      fflush(stdout);
      continue;
    }
    // Model preparation reports success or failure through the audio pipe.
    if (source == UINT32_MAX && count == 0 && rate <= 1 && correction.directory) {
      if (!correction.preparationReported)
        correction_diagnostic(rate == 1 ? "app_reported_prepared" : "app_reported_preparation_failed");
      correction_report_preparation(&correction, rate == 1 && correction_files_ready(&correction, 1));
      continue;
    }
    if (source > 1 || rate < 8000 || rate > 384000 || count == 0 || count > sizeof(samples) / sizeof(samples[0]) ||
        fread(samples, sizeof(float), count, stdin) != count) {
      puts("A\tinvalid_audio_packet");
      fflush(stdout);
      status = 5;
      break;
    }
    const SherpaOnnxOnlineStream *stream = streams[source];
    AudioActivity *audio = &activity[source];
    double packet_start = audio->received;
    if (packet_start == 0) {
      printf("A\tfirst_packet source=%u sample_rate=%u\n", source, rate);
      fflush(stdout);
    }
    audio->received += count / (double)rate;
    if (correction.started && !correction.preparationReported && packet_start >= next_correction_check[source]) {
      if (correction_files_ready(&correction, 0)) correction_report_preparation(&correction, 1);
      else if (packet_start >= 60) {
        correction_diagnostic("engine_preparation_timed_out");
        correction_report_preparation(&correction, 0);
      }
      next_correction_check[source] = packet_start + 1;
    }
    correction_audio(&correction, source, rate, samples, count, packet_start);
    int has_signal = has_input(samples, count);
    if (has_signal && !audio->input_detected) {
      audio->input_detected = 1;
      printf("A\tfirst_signal source=%u offset_ms=%.0f\n", source, packet_start * 1000);
      fflush(stdout);
    }
    int use_microphone_vad = source == 1 && microphone_vad.detector;
    if (use_microphone_vad)
      has_signal = microphone_vad_detect(&microphone_vad, rate, samples, count, has_signal);
    if (has_signal) {
      if (audio->last_signal == 0 && use_microphone_vad) {
        printf("A\tfirst_microphone_speech offset_ms=%.0f\n", packet_start * 1000);
        fflush(stdout);
      }
      if (audio->last_signal == 0 || packet_start - audio->last_signal >= 0.65)
        audio->signal_start = packet_start;
      audio->last_signal = audio->received;
    }
    if (online_speakers) diar_enqueue(&diar, source, rate, count, samples, has_signal);
    DiarizedCaption *diarized = &diarized_captions[source];
    if (online_speakers && !drafts[source] && *diarized->text &&
        ((!has_signal && !audio->active && audio->received - audio->last_signal >= ParagraphSilence) ||
         (diarized->last_word_time > 0 && audio->received - diarized->last_word_time >= ParagraphSilence))) {
      DiarTimeline timeline = diar_snapshot(&diar, source);
      CaptionRange isolated_audio = audio->received - audio->last_signal >= 0.6
        ? (CaptionRange){fmax(0, audio->signal_start - 0.16), audio->last_signal + 0.20} : (CaptionRange){0};
      publish_diarized_caption(diarized, source, NULL, 1, 0, &timeline, 1, &correction, isolated_audio, audio->last_signal);
      fflush(stdout);
    }
    if (!audio->active) {
      if (!has_signal && remember_quiet(audio, samples, count, rate)) continue;
      audio->stream_origin = packet_start;
      if (audio->count && audio->capacity == rate / 5) {
        audio->stream_origin -= audio->count / (double)rate;
        OnlineStreamAcceptWaveform(stream, rate, audio->preroll, (int32_t)audio->count);
      }
      audio->count = 0;
      audio->active = 1;
    }
    OnlineStreamAcceptWaveform(stream, rate, samples, count);
    audio->quiet = has_signal ? 0 : audio->quiet + count / (double)rate;
    // The transducer commits in chunks and can defer a silence endpoint until
    // the next voice arrives. Close on sustained input silence as a backstop.
    DiarTimeline timeline = {0};
    if (online_speakers) timeline = diar_snapshot(&diar, source);
    double pause = use_microphone_vad ? microphone_pause : 1.6;
    int silence_endpoint = (online_speakers || use_microphone_vad) && audio->quiet >= pause &&
      (!online_speakers || timeline.covered >= audio->received - audio->quiet + 0.32 || audio->quiet >= 3.0);
    if (silence_endpoint) {
      // Give the last chunk its right context before finalizing the stream.
      for (uint32_t padding = rate / 2; padding;) {
        uint32_t block = padding < 8192 ? padding : 8192;
        OnlineStreamAcceptWaveform(stream, rate, tail_padding, (int32_t)block);
        padding -= block;
      }
      OnlineStreamInputFinished(stream);
    }
    int ready;
    while ((ready = IsOnlineStreamReady(recognizer, stream)) || silence_endpoint) {
      if (ready) {
        DecodeOnlineStream(recognizer, stream);
        if (silence_endpoint) continue;
      }
      const SherpaOnnxOnlineRecognizerResult *result = GetOnlineStreamResult(recognizer, stream);
      int final = silence_endpoint || OnlineStreamIsEndpoint(recognizer, stream);
      if (online_speakers && !silence_endpoint && result && result->text && *result->text &&
          timeline.covered < last_token_time(result, audio->stream_origin) - 0.25) final = 0;
      if (result && result->text && *result->text) {
        const char *text = result->text;
        if (online_speakers) {
          publish_diarized_caption(diarized, source, result, final, audio->stream_origin, &timeline, 0, &correction, (CaptionRange){0}, audio->last_signal);
          scored_through[source] = timeline.covered;
          if (drafts[source]) DestroyOnlineRecognizerResult(drafts[source]);
          drafts[source] = final ? NULL : result;
          if (!final) result = NULL;
        } else {
          CaptionParagraph *paragraph = &paragraphs[source];
          size_t length = strlen(text);
          size_t stable = paragraph_stable(paragraph, text, final);
          CaptionWord words[DiarizedWords * 2];
          size_t word_count = append_caption_words(words, 0, 0, 0, length, result, audio->stream_origin);
          CaptionRange fallback = {audio->stream_origin, audio->last_signal};
          size_t start = paragraph->published < length ? paragraph->published : length;
          while (start < length && ((unsigned char)text[start] & 0xc0) == 0x80) ++start;
          while (start < length && text[start] == ' ') ++start;
          // A readable paragraph can close without resetting the acoustic decoder.
          size_t cut = stable > start ? paragraph_boundary(text + start, stable - start) : 0;
          while (cut) {
            finalize_caption(&correction, source, 0, text, start, start + cut, words, word_count, fallback, (CaptionRange){0});
            start += cut;
            paragraph->published = start;
            while (start < length && text[start] == ' ') ++start;
            cut = stable > start ? paragraph_boundary(text + start, stable - start) : 0;
          }
          if (start < length) {
            if (final) {
              CaptionRange isolated_audio = start == 0 && audio->quiet >= 0.6
                ? (CaptionRange){audio->stream_origin, audio->last_signal + 0.20} : (CaptionRange){0};
              finalize_caption(&correction, source, 0, text, start, length, words, word_count, fallback, isolated_audio);
            } else {
              emit_caption(source, '0', 0, 0, caption_range(words, word_count, start, length, fallback),
                           text + start, length - start);
            }
          }
        }
        fflush(stdout);
      }
      if (result) DestroyOnlineRecognizerResult(result);
      if (final) {
        if (drafts[source]) {
          publish_diarized_caption(diarized, source, drafts[source], 1, audio->stream_origin, &timeline, 0, &correction, (CaptionRange){0}, audio->last_signal);
          DestroyOnlineRecognizerResult(drafts[source]);
          drafts[source] = NULL;
        }
        if (!has_signal && (!online_speakers || silence_endpoint)) audio->active = 0;
        audio->quiet = 0;
        // A new audio origin requires a new stream. Reset alone keeps
        // Sherpa's accumulated timestamp offset from the previous utterance.
        if (!audio->active) {
          DestroyOnlineStream(stream);
          streams[source] = CreateOnlineStream(recognizer);
          if (!streams[source]) status = 3;
        } else {
          OnlineStreamReset(recognizer, stream);
        }
        paragraphs[source] = (CaptionParagraph){0};
        if (!audio->active) break;
      }
    }
    if (status) break;
    if (online_speakers && drafts[source] && timeline.covered > scored_through[source]) {
      publish_diarized_caption(diarized, source, drafts[source], 0, audio->stream_origin, &timeline, 0, &correction, (CaptionRange){0}, audio->last_signal);
      scored_through[source] = timeline.covered;
      fflush(stdout);
    }
  }
  if (online_speakers) {
    diar_join(&diar);
    for (int i = 0; i < AudioSources; ++i) {
      DiarTimeline timeline = diar_snapshot(&diar, (uint32_t)i);
      publish_diarized_caption(&diarized_captions[i], (uint32_t)i, drafts[i], 1, activity[i].stream_origin, &timeline, 1, &correction, (CaptionRange){0}, activity[i].last_signal);
      if (drafts[i]) DestroyOnlineRecognizerResult(drafts[i]);
    }
    diar_destroy(&diar);
  }
  for (int i = 0; i < AudioSources; ++i) {
    free(activity[i].preroll);
    if (streams[i]) DestroyOnlineStream(streams[i]);
  }
  microphone_vad_finish(&microphone_vad);
  correction_finish(&correction);
  DestroyOnlineRecognizer(recognizer);
  return status;
}
