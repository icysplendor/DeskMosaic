#ifndef TRANSLATION_MICROPHONE_VAD_H_
#define TRANSLATION_MICROPHONE_VAD_H_

#define VAD_API(name) __typeof__(&SherpaOnnx##name) name
typedef struct {
  VAD_API(CreateVoiceActivityDetector);
  VAD_API(DestroyVoiceActivityDetector);
  VAD_API(VoiceActivityDetectorAcceptWaveform);
  VAD_API(VoiceActivityDetectorDetected);
  VAD_API(VoiceActivityDetectorEmpty);
  VAD_API(VoiceActivityDetectorPop);
  VAD_API(VoiceActivityDetectorReset);
  VAD_API(CreateLinearResampler);
  VAD_API(DestroyLinearResampler);
  VAD_API(LinearResamplerResample);
  VAD_API(LinearResamplerResampleFree);
  const SherpaOnnxVoiceActivityDetector *detector;
  const SherpaOnnxLinearResampler *resampler;
  uint32_t rate;
} MicrophoneVad;
#undef VAD_API

static void microphone_vad_start(MicrophoneVad *vad, const char *model, float threshold) {
  if (!model || !*model || !vad->CreateVoiceActivityDetector || !vad->DestroyVoiceActivityDetector ||
      !vad->VoiceActivityDetectorAcceptWaveform || !vad->VoiceActivityDetectorDetected ||
      !vad->VoiceActivityDetectorEmpty || !vad->VoiceActivityDetectorPop || !vad->VoiceActivityDetectorReset ||
      !vad->CreateLinearResampler || !vad->DestroyLinearResampler ||
      !vad->LinearResamplerResample || !vad->LinearResamplerResampleFree) return;

  SherpaOnnxVadModelConfig config = {0};
  config.silero_vad.model = model;
  config.silero_vad.threshold = threshold;
  config.silero_vad.min_silence_duration = 0.25f;
  config.silero_vad.min_speech_duration = 0.10f;
  config.silero_vad.max_speech_duration = 60.0f;
  config.silero_vad.window_size = 512;
  config.sample_rate = 16000;
  config.num_threads = 1;
  config.provider = "cpu";
  vad->detector = vad->CreateVoiceActivityDetector(&config, 10.0f);
}

static int microphone_vad_detect(MicrophoneVad *vad, uint32_t rate, const float *samples, uint32_t count, int energy) {
  if (!vad->detector) return energy;
  if (rate != vad->rate) {
    if (vad->resampler) vad->DestroyLinearResampler(vad->resampler);
    if (vad->rate) vad->VoiceActivityDetectorReset(vad->detector);
    vad->resampler = rate == 16000 ? NULL : vad->CreateLinearResampler(rate, 16000, 0, 0);
    vad->rate = rate;
  }

  if (rate == 16000) {
    vad->VoiceActivityDetectorAcceptWaveform(vad->detector, samples, (int32_t)count);
  } else {
    if (!vad->resampler) return energy;
    const SherpaOnnxResampleOut *output = vad->LinearResamplerResample(vad->resampler, samples, (int32_t)count, 0);
    if (!output) return energy;
    if (output->n > 0) vad->VoiceActivityDetectorAcceptWaveform(vad->detector, output->samples, output->n);
    vad->LinearResamplerResampleFree(output);
  }
  while (!vad->VoiceActivityDetectorEmpty(vad->detector)) vad->VoiceActivityDetectorPop(vad->detector);
  return vad->VoiceActivityDetectorDetected(vad->detector);
}

static void microphone_vad_finish(MicrophoneVad *vad) {
  if (vad->resampler) vad->DestroyLinearResampler(vad->resampler);
  if (vad->detector) vad->DestroyVoiceActivityDetector(vad->detector);
}

#endif
