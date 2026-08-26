from __future__ import annotations

import importlib.util
import contextlib
import io
import json
import math
import os
import base64
import subprocess
import threading
import time
from array import array
from pathlib import Path
import sys
import tempfile
import types
import unittest
import warnings


WORKER_PATH = Path(__file__).with_name("speaker_worker.py")
SPEC = importlib.util.spec_from_file_location("speaker_worker", WORKER_PATH)
assert SPEC and SPEC.loader
speaker_worker = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = speaker_worker
SPEC.loader.exec_module(speaker_worker)


def tone_pcm(duration_seconds: int, amplitude: int = 1000, frequency: int = 220) -> bytes:
    samples = array("h")
    total = speaker_worker.SAMPLE_RATE * duration_seconds
    for index in range(total):
        value = int(math.sin(2.0 * math.pi * frequency * index / speaker_worker.SAMPLE_RATE) * amplitude)
        samples.append(value)
    return samples.tobytes()


class BrokenSnapshotCache:
    def exists(self) -> bool:
        return True

    def glob(self, pattern: str):
        raise OSError("[WinError 448] untrusted mount point")

    def __truediv__(self, name: str):
        if name == "snapshots":
            return BrokenSnapshots()
        raise AssertionError(f"unexpected child path: {name}")


class BrokenSnapshots:
    def is_dir(self) -> bool:
        return True

    def iterdir(self):
        raise OSError("[WinError 448] untrusted mount point")


class WorkerCacheTests(unittest.TestCase):
    def wait_for_streaming_diarization(self, engine: speaker_worker.LocalSpeechEngine, timeout: float = 2.0) -> None:
        thread = engine.streaming_diarization_thread
        if thread is not None:
            thread.join(timeout)
            self.assertFalse(thread.is_alive())

    def wait_for_transcription(self, worker: speaker_worker.StreamWorker, timeout: float = 2.0) -> None:
        thread = getattr(worker, "transcription_thread", None)
        if thread is not None:
            thread.join(timeout)
            self.assertFalse(thread.is_alive())

    def test_configure_huggingface_cache_uses_app_model_directory(self) -> None:
        original = {name: os.environ.get(name) for name in ["HF_HOME", "HUGGINGFACE_HUB_CACHE", "HF_HUB_CACHE"]}
        try:
            speaker_worker.configure_huggingface_cache(Path("models"))

            self.assertEqual(str(Path("models") / "huggingface"), os.environ["HF_HOME"])
            self.assertEqual(str(Path("models") / "huggingface" / "hub"), os.environ["HUGGINGFACE_HUB_CACHE"])
            self.assertEqual(str(Path("models") / "huggingface" / "hub"), os.environ["HF_HUB_CACHE"])
        finally:
            for name, value in original.items():
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value

    def test_speaker_worker_imports_support_modules_without_worker_on_sys_path(self) -> None:
        code = """
import importlib.util
import sys
from pathlib import Path

worker = Path("worker/speaker_worker.py").resolve()
worker_dir = str(worker.parent)
sys.path = [entry for entry in sys.path if str(Path(entry or ".").resolve()) != worker_dir]
spec = importlib.util.spec_from_file_location("isolated_speaker_worker", worker)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)
print("ok")
"""
        result = subprocess.run(
            [sys.executable, "-c", code],
            cwd=Path(__file__).resolve().parents[1],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("ok", result.stdout)

    def test_stream_worker_flushes_after_sensitive_preset_chunk(self) -> None:
        calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.engine.transcribe = lambda source, pcm, timestamp_ms, queue_diarization=True: calls.append((source, pcm, timestamp_ms, queue_diarization))

        payload = base64.b64encode(b"\0" * speaker_worker.BYTES_PER_SECOND * 2).decode("ascii")

        worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        })
        self.wait_for_transcription(worker)

        self.assertEqual(1, len(calls))
        self.assertEqual("system", calls[0][0])
        self.assertEqual(speaker_worker.BYTES_PER_SECOND * 2, len(calls[0][1]))
        self.assertEqual(1234, calls[0][2])
        self.assertTrue(calls[0][3])

    def test_stream_worker_ignores_removed_live_captions_engine_payload(self) -> None:
        calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.engine.configure({
            "type": "configure",
            "sttEngine": "windows_live_captions",
            "diarizationEnabled": True,
            "sttQualityPreset": 0,
        })
        worker.engine.transcribe = lambda source, pcm, timestamp_ms, queue_diarization=True: calls.append((source, len(pcm), timestamp_ms, queue_diarization))
        worker.engine.diarize_audio = lambda *_args, **_kwargs: self.fail("removed Live Captions mode must not run diarization-only chunks")

        payload = base64.b64encode(b"\0" * speaker_worker.BYTES_PER_SECOND * 2).decode("ascii")

        worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        })
        self.wait_for_transcription(worker)

        self.assertEqual([("system", speaker_worker.BYTES_PER_SECOND * 2, 1234, True)], calls)

    def test_stream_worker_start_loads_models_before_listening(self) -> None:
        events = []
        order = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event) or order.append(event.get("stage", event.get("code")))
        try:
            worker = speaker_worker.StreamWorker(Path("models"))
            worker.engine.config.diarization_enabled = False

            def ensure_loaded():
                order.append("ensure_loaded")
                worker.engine.whisper_model = object()

            worker.engine.ensure_loaded = ensure_loaded

            worker.handle({"type": "start"})
        finally:
            speaker_worker.emit = original_emit

        self.assertTrue(worker.running)
        self.assertLess(order.index("ensure_loaded"), order.index("listening"))

    def test_stream_worker_start_does_not_listen_when_models_fail(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            worker = speaker_worker.StreamWorker(Path("models"))
            worker.engine.config.diarization_enabled = False
            worker.engine.ensure_loaded = lambda: None

            worker.handle({"type": "start"})
        finally:
            speaker_worker.emit = original_emit

        self.assertFalse(worker.running)
        self.assertFalse(any(event.get("stage") == "listening" for event in events))
        self.assertTrue(any(event.get("stage") == "setup_failed" for event in events))

    def test_stream_worker_feeds_diart_before_stt_chunk_flush_in_speed_mode(self) -> None:
        diarization_calls = []
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.engine.config.diarization_model = "diart"
        worker.engine.config.stt_quality_preset = 50
        worker.engine.queue_streaming_diarization = lambda source, pcm, timestamp_ms=0: diarization_calls.append((source, len(pcm), timestamp_ms))
        worker.engine.transcribe = lambda source, pcm, timestamp_ms, queue_diarization=True: transcribe_calls.append((source, len(pcm), timestamp_ms, queue_diarization))

        payload = base64.b64encode(b"\1" * speaker_worker.BYTES_PER_SECOND).decode("ascii")

        worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        })

        half_second_bytes = int(speaker_worker.BYTES_PER_SECOND * speaker_worker.DIART_STREAM_CHUNK_SECONDS)
        self.assertEqual([
            ("system", half_second_bytes, 1234),
            ("system", half_second_bytes, 1734),
        ], diarization_calls)
        self.assertEqual([], transcribe_calls)

    def test_stream_worker_feeds_diart_in_accuracy_mode_before_stt_flush(self) -> None:
        diarization_calls = []
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.engine.config.diarization_model = "diart"
        worker.engine.config.stt_quality_preset = 100
        worker.engine.queue_streaming_diarization = lambda source, pcm, timestamp_ms=0: diarization_calls.append((source, len(pcm), timestamp_ms))
        worker.engine.transcribe = lambda source, pcm, timestamp_ms, queue_diarization=True: transcribe_calls.append((source, len(pcm), timestamp_ms, queue_diarization))

        payload = base64.b64encode(b"\1" * speaker_worker.BYTES_PER_SECOND).decode("ascii")

        worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        })

        half_second_bytes = int(speaker_worker.BYTES_PER_SECOND * speaker_worker.DIART_STREAM_CHUNK_SECONDS)
        self.assertEqual([
            ("system", half_second_bytes, 1234),
            ("system", half_second_bytes, 1734),
        ], diarization_calls)
        self.assertEqual([], transcribe_calls)

    def test_stream_worker_does_not_duplicate_diart_when_stt_flushes(self) -> None:
        diarization_calls = []
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.engine.config.diarization_model = "diart"
        worker.engine.config.stt_quality_preset = 100
        worker.engine.queue_streaming_diarization = lambda source, pcm, timestamp_ms=0: diarization_calls.append((source, len(pcm), timestamp_ms))
        worker.engine.transcribe = lambda source, pcm, timestamp_ms, queue_diarization=True: transcribe_calls.append((source, len(pcm), timestamp_ms, queue_diarization))

        payload = base64.b64encode(b"\1" * speaker_worker.BYTES_PER_SECOND * 5).decode("ascii")

        worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        })
        self.wait_for_transcription(worker)

        half_second_bytes = int(speaker_worker.BYTES_PER_SECOND * speaker_worker.DIART_STREAM_CHUNK_SECONDS)
        self.assertEqual(10, len(diarization_calls))
        self.assertEqual(("system", half_second_bytes, 1234), diarization_calls[0])
        self.assertEqual(("system", half_second_bytes, 5734), diarization_calls[-1])
        self.assertEqual([("system", speaker_worker.BYTES_PER_SECOND * 5, 1234, False)], transcribe_calls)

    def test_stream_worker_runs_asr_in_background(self) -> None:
        started = threading.Event()
        release = threading.Event()
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        pcm = tone_pcm(1)
        worker.engine.transcribe_chunk_bytes = lambda: len(pcm)

        def transcribe(source, pcm_data, timestamp_ms, queue_diarization=True):
            transcribe_calls.append((source, len(pcm_data), timestamp_ms, queue_diarization))
            started.set()
            release.wait(1.0)

        worker.engine.transcribe = transcribe
        payload = base64.b64encode(pcm).decode("ascii")

        thread = threading.Thread(target=lambda: worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        }))

        try:
            thread.start()
            self.assertTrue(started.wait(0.5))
            thread.join(0.05)
            self.assertFalse(thread.is_alive(), "ASR must not block the audio input handler.")
        finally:
            release.set()
            thread.join(1.0)
            self.wait_for_transcription(worker)

        self.assertEqual([("system", len(pcm), 1234, True)], transcribe_calls)

    def test_stream_worker_replaces_stale_pending_chunks_per_source(self) -> None:
        started = threading.Event()
        release = threading.Event()
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        pcm = tone_pcm(1)

        def transcribe(source, pcm_data, timestamp_ms, queue_diarization=True):
            transcribe_calls.append((source, len(pcm_data), timestamp_ms, queue_diarization))
            if timestamp_ms == 1000:
                started.set()
                release.wait(1.0)

        worker.engine.transcribe = transcribe
        output = io.StringIO()
        try:
            with contextlib.redirect_stdout(output):
                worker._enqueue_transcription("system", pcm, 1000, True)
                self.assertTrue(started.wait(0.5))
                worker._enqueue_transcription("mic", pcm, 2000, True)
                worker._enqueue_transcription("system", pcm, 3000, True)
                worker._enqueue_transcription("system", pcm, 4000, True)
                worker._enqueue_transcription("mic", pcm, 5000, True)

            with worker.transcription_lock:
                queued = list(worker.transcription_queue.queue)
            self.assertEqual(2, len(queued))
            self.assertEqual({("system", 4000), ("mic", 5000)}, {(item[1], item[3]) for item in queued})
        finally:
            release.set()
            self.wait_for_transcription(worker)

        events = [json.loads(line) for line in output.getvalue().splitlines()]
        backpressure_events = [event for event in events if event.get("stage") == "asr_backpressure"]
        self.assertEqual(1, len(backpressure_events))
        self.assertNotIn(("mic", len(pcm), 2000, True), transcribe_calls)
        self.assertNotIn(("system", len(pcm), 3000, True), transcribe_calls)

    def test_stream_worker_runs_streaming_diarization_asr_in_background(self) -> None:
        started = threading.Event()
        release = threading.Event()
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.engine.config.diarization_model = "diart"
        worker.engine.queue_streaming_diarization = lambda *_args, **_kwargs: None
        pcm = tone_pcm(1)
        worker.engine.transcribe_chunk_bytes = lambda: len(pcm)

        def transcribe(source, pcm_data, timestamp_ms, queue_diarization=True):
            transcribe_calls.append((source, len(pcm_data), timestamp_ms, queue_diarization))
            started.set()
            release.wait(1.0)

        worker.engine.transcribe = transcribe
        payload = base64.b64encode(pcm).decode("ascii")

        thread = threading.Thread(target=lambda: worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        }))

        try:
            thread.start()
            self.assertTrue(started.wait(0.5))
            thread.join(0.05)
            self.assertFalse(thread.is_alive(), "Streaming diarization ASR must not block the audio input handler.")
        finally:
            release.set()
            thread.join(1.0)
            self.wait_for_transcription(worker)

        self.assertEqual([("system", len(pcm), 1234, False)], transcribe_calls)

    def test_stream_worker_starts_new_asr_thread_after_generation_reset(self) -> None:
        first_started = threading.Event()
        second_started = threading.Event()
        release_first = threading.Event()
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        pcm = tone_pcm(1)
        worker.engine.transcribe_chunk_bytes = lambda: len(pcm)

        def transcribe(source, pcm_data, timestamp_ms, queue_diarization=True):
            transcribe_calls.append((source, len(pcm_data), timestamp_ms, queue_diarization))
            if timestamp_ms == 1000:
                first_started.set()
                release_first.wait(1.0)
            elif timestamp_ms == 2000:
                second_started.set()

        worker.engine.transcribe = transcribe
        payload = base64.b64encode(pcm).decode("ascii")

        try:
            worker.handle({
                "type": "audio_chunk",
                "source": "system",
                "data": payload,
                "timestampMs": 1000,
            })
            self.assertTrue(first_started.wait(0.5))

            worker._invalidate_transcription_queue()
            worker.handle({
                "type": "audio_chunk",
                "source": "system",
                "data": payload,
                "timestampMs": 2000,
            })

            self.assertTrue(second_started.wait(0.5), "A reset generation must not wait behind the old ASR thread.")
        finally:
            release_first.set()
            self.wait_for_transcription(worker)

        self.assertIn(("system", len(pcm), 1000, True), transcribe_calls)
        self.assertIn(("system", len(pcm), 2000, True), transcribe_calls)

    def test_stream_worker_replaces_idle_live_asr_thread(self) -> None:
        class IdleThread:
            def is_alive(self) -> bool:
                return True

        started = threading.Event()
        transcribe_calls = []
        worker = speaker_worker.StreamWorker(Path("models"))
        worker.running = True
        worker.transcription_thread = IdleThread()
        worker.transcription_thread_generation = worker.transcription_generation
        pcm = tone_pcm(1)
        worker.engine.transcribe_chunk_bytes = lambda: len(pcm)

        def transcribe(source, pcm_data, timestamp_ms, queue_diarization=True):
            transcribe_calls.append((source, len(pcm_data), timestamp_ms, queue_diarization))
            started.set()

        worker.engine.transcribe = transcribe
        payload = base64.b64encode(pcm).decode("ascii")

        worker.handle({
            "type": "audio_chunk",
            "source": "system",
            "data": payload,
            "timestampMs": 1234,
        })
        self.assertTrue(started.wait(0.5), "An idle live ASR thread must not prevent queue consumption.")
        self.wait_for_transcription(worker)

        self.assertEqual([("system", len(pcm), 1234, True)], transcribe_calls)

    def test_diarize_audio_skips_silent_audio(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine._speaker_for = lambda *_args, **_kwargs: self.fail("silent audio should not run diarization")

            engine.diarize_audio("system", b"\0" * speaker_worker.BYTES_PER_SECOND, 0)
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual([], events)

    def test_transcribe_skips_silent_audio_before_running_whisper(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: self.fail("silent audio should not load STT models")
            engine._speaker_for = lambda *_args, **_kwargs: self.fail("silent audio should not run diarization")
            engine._transcribe_wav = lambda _wav_path: self.fail("silent audio should not run Whisper")
            engine._transcribe_wav_parts = lambda _wav_path: self.fail("silent audio should not run Whisper")

            engine.transcribe("system", b"\0" * speaker_worker.BYTES_PER_SECOND, 0)
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual([], events)

    def test_diarize_audio_emits_speaker_segments_without_extra_switch_delay(self) -> None:
        events = []
        speakers = iter(["speaker_2", "speaker_2", "speaker_2", "speaker_3", "speaker_3"])
        original_emit = speaker_worker.emit
        original_voice = speaker_worker.pcm_has_voice
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_has_voice = lambda _pcm: True
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine._speaker_for = lambda *_args, **_kwargs: next(speakers)

            for index in range(5):
                engine.diarize_audio("system", b"\1" * speaker_worker.BYTES_PER_SECOND, index * 1000)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_has_voice = original_voice

        self.assertEqual(["speaker_2", "speaker_2", "speaker_3"], [event["speakerId"] for event in events])

    def test_diarize_audio_waits_for_minimum_context_before_running_pyannote(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        original_voice = speaker_worker.pcm_has_voice
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_has_voice = lambda _pcm: True
        case = self

        class FakePipeline:
            def __call__(self, *_args, **_kwargs):
                case.fail("short rolling context should not run pyannote yet")

        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.diarization_pipeline = FakePipeline()
            for index in range(speaker_worker.DIARIZATION_MIN_CONTEXT_SECONDS - 1):
                engine.diarize_audio("system", b"\1" * speaker_worker.BYTES_PER_SECOND, index * 1000)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_has_voice = original_voice

        self.assertEqual([], events)

    def test_stt_preprocess_trims_silence_with_padding_and_normalizes(self) -> None:
        silence = b"\0" * int(speaker_worker.BYTES_PER_SECOND * 0.4)
        pcm = silence + tone_pcm(1, amplitude=700) + silence

        result = speaker_worker.preprocess_stt_pcm(pcm)

        self.assertIsNotNone(result)
        assert result is not None
        self.assertGreater(result.leading_trim_ms, 100)
        self.assertLess(result.leading_trim_ms, 300)
        self.assertGreater(result.trailing_trim_ms, 100)
        self.assertLess(result.trailing_trim_ms, 300)
        self.assertLess(len(result.pcm), len(pcm))
        samples = speaker_worker.pcm_to_samples(result.pcm)
        self.assertGreater(max(abs(sample) for sample in samples), 2000)

    def test_stt_preprocess_preserves_continuous_voice_for_streaming_diarization(self) -> None:
        pcm = tone_pcm(5, amplitude=220)

        default_result = speaker_worker.preprocess_stt_pcm(pcm)
        streaming_result = speaker_worker.preprocess_stt_pcm(pcm, preserve_continuous_voice=True)

        self.assertIsNone(default_result)
        self.assertIsNotNone(streaming_result)
        assert streaming_result is not None
        self.assertEqual(0, streaming_result.leading_trim_ms)
        self.assertEqual(0, streaming_result.trailing_trim_ms)
        self.assertEqual(len(pcm), len(streaming_result.pcm))

    def test_streaming_diarization_transcribes_continuous_voice_instead_of_dropping_it(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.config.asr_engine = "qwen3_asr_diarization"
            engine.config.diarization_enabled = True
            engine.config.diarization_model = "sortformer"
            engine.ensure_loaded = lambda: None
            engine._transcribe_wav_parts = lambda _path: [
                speaker_worker.TimedTextPart("지속 음성 자막", 0, 5000, False)
            ]

            engine.transcribe("system", tone_pcm(5, amplitude=220), 1000, queue_diarization=False)
        finally:
            speaker_worker.emit = original_emit

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(1, len(captions))
        self.assertEqual("지속 음성 자막", captions[0]["text"])
        self.assertFalse(any(event.get("stage") == "asr_audio_skipped" for event in events))

    def test_streaming_diarization_requires_diarization_to_be_enabled(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "sortformer"
        engine.config.diarization_enabled = False

        self.assertFalse(engine._uses_streaming_diarization())

    def test_has_whisper_model_bin_treats_untrusted_windows_cache_as_missing(self) -> None:
        original = speaker_worker.whisper_cache_dir
        speaker_worker.whisper_cache_dir = lambda _models_dir, _model_name: BrokenSnapshotCache()
        try:
            self.assertFalse(speaker_worker.has_whisper_model_bin(Path("models"), "small"))
        finally:
            speaker_worker.whisper_cache_dir = original

    def test_load_pyannote_pipeline_uses_app_cache_and_falls_back_to_use_auth_token(self) -> None:
        calls = []

        class FakePipeline:
            @staticmethod
            def from_pretrained(model_id, **kwargs):
                calls.append(kwargs)
                if "token" in kwargs:
                    raise TypeError("Pipeline.from_pretrained() got an unexpected keyword argument 'token'")
                return "pipeline"

        fake_pyannote = types.ModuleType("pyannote")
        fake_audio = types.ModuleType("pyannote.audio")
        fake_audio.Pipeline = FakePipeline

        original_pyannote = sys.modules.get("pyannote")
        original_audio = sys.modules.get("pyannote.audio")
        sys.modules["pyannote"] = fake_pyannote
        sys.modules["pyannote.audio"] = fake_audio
        try:
            pipeline = speaker_worker.load_pyannote_pipeline("model-id", "hf_test", Path("cache"))
        finally:
            if original_pyannote is None:
                sys.modules.pop("pyannote", None)
            else:
                sys.modules["pyannote"] = original_pyannote
            if original_audio is None:
                sys.modules.pop("pyannote.audio", None)
            else:
                sys.modules["pyannote.audio"] = original_audio

        self.assertEqual("pipeline", pipeline)
        self.assertEqual(
            [
                {"token": "hf_test", "cache_dir": str(Path("cache"))},
                {"use_auth_token": "hf_test", "cache_dir": str(Path("cache"))},
            ],
            calls,
        )

    def test_torchaudio_compatibility_shim_adds_removed_set_audio_backend(self) -> None:
        fake_torchaudio = types.ModuleType("torchaudio")

        original_torchaudio = sys.modules.get("torchaudio")
        sys.modules["torchaudio"] = fake_torchaudio
        try:
            speaker_worker.apply_torchaudio_compatibility_shims()
            self.assertTrue(callable(fake_torchaudio.set_audio_backend))
            fake_torchaudio.set_audio_backend("soundfile")
        finally:
            if original_torchaudio is None:
                sys.modules.pop("torchaudio", None)
            else:
                sys.modules["torchaudio"] = original_torchaudio

    def test_torchcodec_warning_is_suppressed_for_in_memory_audio_pipeline(self) -> None:
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            speaker_worker.suppress_torchcodec_warning()
            warnings.warn(
                "\ntorchcodec is not installed correctly so built-in audio decoding will fail.",
                UserWarning,
            )

        self.assertEqual([], caught)

    def test_speechbrain_lazy_import_shim_avoids_recursion(self) -> None:
        fake_importutils = types.ModuleType("speechbrain.utils.importutils")

        class FakeLazyModule(types.ModuleType):
            def __init__(self, name: str, target: str, package):
                super().__init__(name)
                self.target = target
                self.package = package
                self.lazy_module = None

            def ensure_module(self, _stacklevel: int):
                raise RecursionError("old speechbrain lazy import recursion")

        fake_importutils.LazyModule = FakeLazyModule

        original = sys.modules.get("speechbrain.utils.importutils")
        sys.modules["speechbrain.utils.importutils"] = fake_importutils
        try:
            speaker_worker.patch_speechbrain_lazy_module_inspection()
            lazy = FakeLazyModule("fake_lazy_math", "math", None)

            module = lazy.ensure_module(0)
            inspect_frame_code = compile("lazy.ensure_module(0)", "C:\\Python311\\Lib\\inspect.py", "exec")
            with self.assertRaises(AttributeError):
                exec(inspect_frame_code, {"lazy": lazy})
        finally:
            if original is None:
                sys.modules.pop("speechbrain.utils.importutils", None)
            else:
                sys.modules["speechbrain.utils.importutils"] = original

        self.assertEqual("math", module.__name__)
        self.assertIs(module, lazy.ensure_module(0))

    def test_resolve_stt_device_uses_ctranslate2_cuda_without_torch_cuda(self) -> None:
        original_ct2 = speaker_worker.ctranslate2_cuda_available
        original_torch = speaker_worker.torch_cuda_available
        speaker_worker.ctranslate2_cuda_available = lambda: True
        speaker_worker.torch_cuda_available = lambda: False
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.config.compute_mode = "auto"

            self.assertEqual("cuda", engine._resolve_stt_device())
            self.assertEqual("cpu", engine._resolve_torch_device())
        finally:
            speaker_worker.ctranslate2_cuda_available = original_ct2
            speaker_worker.torch_cuda_available = original_torch

    def test_configure_normalizes_stt_language_selection(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "sttLanguages": ["KO", "auto", "en", "ko", ""],
                "sttQualityPreset": 75,
            })
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual(["ko", "en"], engine.config.stt_languages)
        self.assertEqual(75, engine.config.stt_quality_preset)
        self.assertIn("languages=ko,en", events[-1]["message"])
        self.assertIn("quality=75", events[-1]["message"])

    def test_configure_accepts_diart_diarization_model(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "diarizationModel": "diart",
                "diarizationEnabled": True,
            })
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual("diart", engine.config.diarization_model)
        self.assertIn("diarization_model=diart", events[-1]["message"])

    def test_configure_accepts_manual_diart_tuning_values(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "diarizationModel": "diart",
                "diartManualSettings": True,
                "diartDurationSeconds": 8,
                "diartStepSeconds": 0.25,
                "diartLatencySeconds": 0.8,
                "diartTauActive": 0.576,
                "diartRhoUpdate": 0.915,
                "diartDeltaNew": 0.648,
            })
        finally:
            speaker_worker.emit = original_emit

        self.assertTrue(engine.config.diart_manual_settings)
        self.assertEqual(8, engine.config.diart_duration_seconds)
        self.assertEqual(0.25, engine.config.diart_step_seconds)
        self.assertEqual(0.8, engine.config.diart_latency_seconds)
        self.assertEqual({"tau_active": 0.576, "rho_update": 0.915, "delta_new": 0.648}, engine._diart_hyper_parameters())
        self.assertIn("diart_manual=duration:8s/step:0.25s/latency:0.8s", events[-1]["message"])

    def test_ensure_loaded_uses_diart_loader_when_selected(self) -> None:
        events = []
        calls = []
        original_emit = speaker_worker.emit
        original_token = os.environ.get("HF_TOKEN")
        original_diart_loader = speaker_worker.load_diart_pipeline
        original_pyannote_loader = speaker_worker.load_pyannote_pipeline
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.load_diart_pipeline = lambda token, models_dir, device, quality_preset, max_speakers, duration, step, latency, hyper_parameters: calls.append((token, models_dir, device, quality_preset, max_speakers, duration, step, latency, hyper_parameters)) or "diart"
        speaker_worker.load_pyannote_pipeline = lambda *_args, **_kwargs: self.fail("Diart selection must not load the pyannote Community-1 pipeline")
        os.environ["HF_TOKEN"] = "hf_test"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "diarizationModel": "diart",
                "diarizationEnabled": True,
                "sttQualityPreset": 50,
                "maxSpeakers": 4,
                "exactSpeakers": 2,
            })
            engine.whisper_model = object()

            engine.ensure_loaded()
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.load_diart_pipeline = original_diart_loader
            speaker_worker.load_pyannote_pipeline = original_pyannote_loader
            if original_token is None:
                os.environ.pop("HF_TOKEN", None)
            else:
                os.environ["HF_TOKEN"] = original_token

        self.assertEqual([("hf_test", Path("models") / "diart", "cpu", 50, 2, 6.0, 0.5, 2.0, {"tau_active": 0.555, "rho_update": 0.422, "delta_new": 1.517})], calls)
        self.assertEqual("diart", engine.diarization_pipeline)
        self.assertTrue(any(event["stage"] == "diarization_loaded" and "Diart" in event["message"] for event in events))

    def test_diart_honors_explicit_cuda_compute_mode(self) -> None:
        calls = []
        original_token = os.environ.get("HF_TOKEN")
        original_diart_loader = speaker_worker.load_diart_pipeline
        original_torch = speaker_worker.torch_cuda_available
        speaker_worker.load_diart_pipeline = lambda token, models_dir, device, quality_preset, max_speakers, duration, step, latency, hyper_parameters: calls.append(device) or "diart"
        speaker_worker.torch_cuda_available = lambda: True
        os.environ["HF_TOKEN"] = "hf_test"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "diarizationModel": "diart",
                "diarizationEnabled": True,
                "computeMode": "cuda",
            })
            engine.whisper_model = object()

            engine.ensure_loaded()
        finally:
            speaker_worker.load_diart_pipeline = original_diart_loader
            speaker_worker.torch_cuda_available = original_torch
            if original_token is None:
                os.environ.pop("HF_TOKEN", None)
            else:
                os.environ["HF_TOKEN"] = original_token

        self.assertEqual(["cuda"], calls)

    def test_auto_compute_keeps_diart_on_cpu_but_allows_sortformer_cuda(self) -> None:
        original_torch = speaker_worker.torch_cuda_available
        speaker_worker.torch_cuda_available = lambda: True
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.config.compute_mode = "auto"
            engine.config.diarization_model = "diart"
            self.assertEqual("cpu", engine._resolve_diarization_device())

            engine.config.diarization_model = "sortformer"
            self.assertEqual("cuda", engine._resolve_diarization_device())
        finally:
            speaker_worker.torch_cuda_available = original_torch

    def test_check_environment_does_not_validate_qwen_as_faster_whisper_model(self) -> None:
        original_has_module = speaker_worker.has_module
        original_qwen_runtime_error = speaker_worker.qwen_asr_runtime_error
        original_locked_package_error = speaker_worker.locked_package_error
        original_import_attribute_error = speaker_worker.import_attribute_error
        original_diarization_runtime_error = speaker_worker.selected_diarization_runtime_error
        original_qwen_files_prepared = speaker_worker.qwen_model_files_prepared
        original_materialize = speaker_worker.materialize_model_cache_links
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"),
            "LIVE_DIALOGUE_TRANSLATOR_STT_MODEL": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_STT_MODEL"),
        }
        speaker_worker.has_module = lambda name: name in {"faster_whisper", "qwen_asr", "torch"}
        speaker_worker.qwen_asr_runtime_error = lambda: None
        speaker_worker.locked_package_error = lambda: None
        speaker_worker.import_attribute_error = lambda *_args: None
        speaker_worker.selected_diarization_runtime_error = lambda *_args: None
        speaker_worker.qwen_model_files_prepared = lambda *_args: True
        speaker_worker.materialize_model_cache_links = lambda *_args, **_kwargs: False
        os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = "qwen3_asr_diarization"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_STT_MODEL"] = "qwen3-asr-1.7b"
        stdout = io.StringIO()
        try:
            with contextlib.redirect_stdout(stdout):
                exit_code = speaker_worker.check_environment(Path("models"))
        finally:
            speaker_worker.has_module = original_has_module
            speaker_worker.qwen_asr_runtime_error = original_qwen_runtime_error
            speaker_worker.locked_package_error = original_locked_package_error
            speaker_worker.import_attribute_error = original_import_attribute_error
            speaker_worker.selected_diarization_runtime_error = original_diarization_runtime_error
            speaker_worker.qwen_model_files_prepared = original_qwen_files_prepared
            speaker_worker.materialize_model_cache_links = original_materialize
            for key, value in original_env.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertEqual(0, exit_code)
        payload = json.loads(stdout.getvalue())
        self.assertTrue(payload["sttModelPrepared"])
        self.assertTrue(payload["sttModelLoadable"])
        self.assertIsNone(payload["sttModelError"])

    def test_check_environment_rejects_incompatible_qwen_runtime(self) -> None:
        original_has_module = speaker_worker.has_module
        original_qwen_runtime_error = speaker_worker.qwen_asr_runtime_error
        original_locked_package_error = speaker_worker.locked_package_error
        original_import_attribute_error = speaker_worker.import_attribute_error
        original_diarization_runtime_error = speaker_worker.selected_diarization_runtime_error
        original_qwen_files_prepared = speaker_worker.qwen_model_files_prepared
        original_materialize = speaker_worker.materialize_model_cache_links
        original_engine = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE")
        speaker_worker.has_module = lambda name: name in {"faster_whisper", "qwen_asr", "torch"}
        speaker_worker.qwen_asr_runtime_error = lambda: "huggingface-hub must be below 1.0"
        speaker_worker.locked_package_error = lambda: None
        speaker_worker.import_attribute_error = lambda *_args: None
        speaker_worker.selected_diarization_runtime_error = lambda *_args: None
        speaker_worker.qwen_model_files_prepared = lambda *_args: True
        speaker_worker.materialize_model_cache_links = lambda *_args, **_kwargs: False
        os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = "qwen3_asr_diarization"
        stdout = io.StringIO()
        try:
            with contextlib.redirect_stdout(stdout):
                exit_code = speaker_worker.check_environment(Path("models"))
        finally:
            speaker_worker.has_module = original_has_module
            speaker_worker.qwen_asr_runtime_error = original_qwen_runtime_error
            speaker_worker.locked_package_error = original_locked_package_error
            speaker_worker.import_attribute_error = original_import_attribute_error
            speaker_worker.selected_diarization_runtime_error = original_diarization_runtime_error
            speaker_worker.qwen_model_files_prepared = original_qwen_files_prepared
            speaker_worker.materialize_model_cache_links = original_materialize
            if original_engine is None:
                os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE", None)
            else:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = original_engine

        self.assertEqual(0, exit_code)
        payload = json.loads(stdout.getvalue())
        self.assertFalse(payload["qwenAsrAvailable"])
        self.assertFalse(payload["sttModelPrepared"])
        self.assertFalse(payload["sttModelLoadable"])
        self.assertEqual("huggingface-hub must be below 1.0", payload["sttModelError"])

    def test_qwen_runtime_check_imports_qwen_model_class(self) -> None:
        original_has_module = speaker_worker.has_module
        original_import_module = speaker_worker.importlib.import_module
        imported_modules: list[str] = []

        def fake_import_module(name: str):
            imported_modules.append(name)
            if name == "qwen_asr":
                raise TypeError("check_model_inputs() missing 1 required positional argument: 'func'")
            raise AssertionError(f"unexpected import: {name}")

        speaker_worker.has_module = lambda name: name == "qwen_asr"
        speaker_worker.importlib.import_module = fake_import_module
        try:
            error = speaker_worker.qwen_asr_runtime_error()
        finally:
            speaker_worker.has_module = original_has_module
            speaker_worker.importlib.import_module = original_import_module

        self.assertEqual(["qwen_asr"], imported_modules)
        self.assertEqual(
            "check_model_inputs() missing 1 required positional argument: 'func'",
            error,
        )

    def test_package_lock_covers_every_optional_runtime(self) -> None:
        lock = json.loads((WORKER_PATH.parent / "package-lock.json").read_text(encoding="utf-8"))

        self.assertEqual(1, lock["schemaVersion"])
        self.assertEqual(
            {
                "base",
                "qwen3-asr",
                "whisperlivekit-sortformer",
                "whisperx",
                "mossformer2-ss-16k",
                "sepformer-whamr16k",
            },
            set(lock["scopes"]),
        )

    def test_torchaudio_compatibility_shims_cover_speechbrain_backend_api(self) -> None:
        fake_torchaudio = types.ModuleType("torchaudio")
        original_torchaudio = sys.modules.get("torchaudio")
        sys.modules["torchaudio"] = fake_torchaudio
        try:
            speaker_worker.apply_torchaudio_compatibility_shims()
        finally:
            if original_torchaudio is None:
                sys.modules.pop("torchaudio", None)
            else:
                sys.modules["torchaudio"] = original_torchaudio

        self.assertEqual(["soundfile"], fake_torchaudio.list_audio_backends())
        self.assertEqual("soundfile", fake_torchaudio.get_audio_backend())
        self.assertIsNone(fake_torchaudio.set_audio_backend("soundfile"))

    def test_unsupported_model_combinations_have_stable_errors(self) -> None:
        self.assertIsNone(speaker_worker.unsupported_model_combination_error(
            "qwen3_asr_diarization",
            True,
            "sortformer",
            "none",
        ))
        self.assertIn("Sortformer", speaker_worker.unsupported_model_combination_error(
            "whisperx",
            True,
            "sortformer",
            "none",
        ))
        self.assertIn("WhisperLiveKit", speaker_worker.unsupported_model_combination_error(
            "whisperlivekit_sortformer",
            False,
            "pyannote_community",
            "mossformer2_ss_16k",
        ))
        self.assertIn("WhisperX", speaker_worker.unsupported_model_combination_error(
            "whisperx",
            False,
            "pyannote_community",
            "sepformer_whamr16k",
        ))

    def test_ensure_loaded_rejects_unsupported_combination_before_model_import(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        original_whisperx_loader = speaker_worker.load_whisperx_model
        speaker_worker.emit = events.append
        speaker_worker.load_whisperx_model = lambda *_args, **_kwargs: self.fail("WhisperX loader must not run")
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "asrEngine": "whisperx",
                "sttModel": "large-v3-turbo",
                "diarizationEnabled": True,
                "diarizationModel": "sortformer",
                "speechSeparationModel": "none",
            })
            events.clear()

            engine.ensure_loaded()
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.load_whisperx_model = original_whisperx_loader

        self.assertFalse(engine.whisper_model)
        self.assertIn("Sortformer", engine.last_stt_error)
        self.assertEqual("unsupported_model_combination", events[-1]["code"])

    def test_locked_scope_rejects_version_drift(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            package_path = Path(temp_dir)
            metadata_path = package_path / "example_package-1.2.3.dist-info"
            metadata_path.mkdir()
            (metadata_path / "METADATA").write_text(
                "Metadata-Version: 2.1\nName: example-package\nVersion: 1.2.3\n",
                encoding="utf-8",
            )

            self.assertIsNone(speaker_worker.locked_scope_error(
                "test",
                package_path,
                {"test": {"example-package": "1.2.3"}},
            ))
            self.assertIn("expected 1.2.4", speaker_worker.locked_scope_error(
                "test",
                package_path,
                {"test": {"example-package": "1.2.4"}},
            ))

    def test_locked_scope_distinguishes_cuda_build_from_cpu_build(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            package_path = Path(temp_dir)
            metadata_path = package_path / "torch-2.8.0.dist-info"
            metadata_path.mkdir()
            (metadata_path / "METADATA").write_text(
                "Metadata-Version: 2.1\nName: torch\nVersion: 2.8.0\n",
                encoding="utf-8",
            )

            error = speaker_worker.locked_scope_error(
                "whisperx",
                package_path,
                {"whisperx": {"torch": "2.8.0+cu128"}},
            )

        self.assertIn("expected 2.8.0+cu128", error)

    def test_check_environment_accepts_whisperlivekit_default_model(self) -> None:
        original_has_module = speaker_worker.has_module
        original_materialize = speaker_worker.materialize_model_cache_links
        original_locked_package_error = speaker_worker.locked_package_error
        original_import_attribute_error = speaker_worker.import_attribute_error
        original_whisperlivekit_runtime_error = speaker_worker.whisperlivekit_runtime_error
        original_diarization_runtime_error = speaker_worker.selected_diarization_runtime_error
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"),
            "LIVE_DIALOGUE_TRANSLATOR_STT_MODEL": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_STT_MODEL"),
        }
        speaker_worker.has_module = lambda name: name in {"faster_whisper", "whisperlivekit", "torch"}
        speaker_worker.materialize_model_cache_links = lambda *_args, **_kwargs: False
        speaker_worker.locked_package_error = lambda: None
        speaker_worker.import_attribute_error = lambda *_args: None
        speaker_worker.whisperlivekit_runtime_error = lambda *_args: None
        speaker_worker.selected_diarization_runtime_error = lambda *_args: None
        os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = "whisperlivekit_sortformer"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_STT_MODEL"] = "default"
        stdout = io.StringIO()
        with tempfile.TemporaryDirectory() as temp_dir:
            whisper_marker = Path(temp_dir) / "whisper" / "prepared"
            whisper_marker.mkdir(parents=True)
            try:
                with contextlib.redirect_stdout(stdout):
                    exit_code = speaker_worker.check_environment(Path(temp_dir))
            finally:
                speaker_worker.has_module = original_has_module
                speaker_worker.materialize_model_cache_links = original_materialize
                speaker_worker.locked_package_error = original_locked_package_error
                speaker_worker.import_attribute_error = original_import_attribute_error
                speaker_worker.whisperlivekit_runtime_error = original_whisperlivekit_runtime_error
                speaker_worker.selected_diarization_runtime_error = original_diarization_runtime_error
                for key, value in original_env.items():
                    if value is None:
                        os.environ.pop(key, None)
                    else:
                        os.environ[key] = value

        self.assertEqual(0, exit_code)
        payload = json.loads(stdout.getvalue())
        self.assertTrue(payload["sttModelPrepared"])
        self.assertTrue(payload["sttModelLoadable"])
        self.assertIsNone(payload["sttModelError"])

    def test_load_whisperlivekit_engine_disables_streaming_context_by_default(self) -> None:
        captured_kwargs = {}

        class FakeTranscriptionEngine:
            def __init__(self, **kwargs):
                captured_kwargs.update(kwargs)

        fake_whisperlivekit = types.ModuleType("whisperlivekit")
        fake_whisperlivekit.TranscriptionEngine = FakeTranscriptionEngine
        original_pkg = sys.modules.get("whisperlivekit")
        original_context_tokens = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_MAX_CONTEXT_TOKENS")
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_WLK_MAX_CONTEXT_TOKENS", None)
        try:
            speaker_worker.load_whisperlivekit_engine("default", "ko", "cuda", 3, True)
        finally:
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_context_tokens is None:
                os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_WLK_MAX_CONTEXT_TOKENS", None)
            else:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_MAX_CONTEXT_TOKENS"] = original_context_tokens

        self.assertEqual(0, captured_kwargs.get("max_context_tokens"))

    def test_ensure_loaded_reports_hf_access_denied_for_gated_diart_model(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        original_token = os.environ.get("HF_TOKEN")
        original_diart_loader = speaker_worker.load_diart_pipeline
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.load_diart_pipeline = lambda *_args, **_kwargs: (_ for _ in ()).throw(
            RuntimeError("Cannot access gated repo for pyannote/segmentation. You are not in the authorized list.")
        )
        os.environ["HF_TOKEN"] = "hf_test"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "diarizationModel": "diart",
                "diarizationEnabled": True,
            })
            engine.whisper_model = object()

            engine.ensure_loaded()
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.load_diart_pipeline = original_diart_loader
            if original_token is None:
                os.environ.pop("HF_TOKEN", None)
            else:
                os.environ["HF_TOKEN"] = original_token

        self.assertFalse(engine.diarization_pipeline)
        error = next(event for event in events if event.get("type") == "error")
        self.assertEqual("hf_access_denied", error["code"])
        self.assertIn("pyannote/segmentation", error["message"])
        self.assertIn("pyannote/embedding", error["message"])

    def test_pyannote_model_loader_keeps_non_json_diagnostics_off_stdout(self) -> None:
        class FakeModel:
            @staticmethod
            def from_pretrained(_model_id, **_kwargs):
                print("Could not download Model from pyannote/segmentation.")
                raise RuntimeError("Cannot access gated repo for pyannote/segmentation.")

        fake_diart = types.ModuleType("diart")
        fake_diart_models = types.ModuleType("diart.models")
        fake_diart_models.PowersetAdapter = lambda model: model
        fake_pyannote = types.ModuleType("pyannote")
        fake_audio = types.ModuleType("pyannote.audio")
        fake_audio.Model = FakeModel

        originals = {name: sys.modules.get(name) for name in ("diart", "diart.models", "pyannote", "pyannote.audio")}
        sys.modules["diart"] = fake_diart
        sys.modules["diart.models"] = fake_diart_models
        sys.modules["pyannote"] = fake_pyannote
        sys.modules["pyannote.audio"] = fake_audio
        try:
            stdout = io.StringIO()
            with self.assertRaises(RuntimeError), contextlib.redirect_stdout(stdout):
                speaker_worker.load_diart_pyannote_model("pyannote/segmentation", "hf_test")
        finally:
            for name, original in originals.items():
                if original is None:
                    sys.modules.pop(name, None)
                else:
                    sys.modules[name] = original

        self.assertEqual("", stdout.getvalue())

    def test_model_load_helper_keeps_success_warnings_off_stderr(self) -> None:
        def noisy_success():
            print("Lightning automatically upgraded your loaded checkpoint", file=sys.stderr)
            return "ok"

        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            result = speaker_worker.call_without_stdout_noise(noisy_success)

        self.assertEqual("ok", result)
        self.assertEqual("", stderr.getvalue())

    def test_quality_preset_controls_transcription_options(self) -> None:
        fast = speaker_worker.whisper_transcribe_options(0)
        balanced = speaker_worker.whisper_transcribe_options(50)
        accurate = speaker_worker.whisper_transcribe_options(100)

        self.assertEqual(1, fast["beam_size"])
        self.assertEqual(3, balanced["beam_size"])
        self.assertEqual(5, accurate["beam_size"])
        self.assertEqual(2, speaker_worker.transcribe_chunk_seconds_for_quality(0))
        self.assertEqual(4, speaker_worker.transcribe_chunk_seconds_for_quality(50))
        self.assertEqual(5, speaker_worker.transcribe_chunk_seconds_for_quality(100))
        self.assertEqual(30, speaker_worker.diarization_context_seconds_for_quality(0))
        self.assertEqual(60, speaker_worker.diarization_context_seconds_for_quality(50))
        self.assertEqual(120, speaker_worker.diarization_context_seconds_for_quality(100))
        self.assertTrue(balanced["word_timestamps"])
        self.assertTrue(accurate["word_timestamps"])
        self.assertEqual(0.5, speaker_worker.diart_latency_seconds_for_quality(0))
        self.assertEqual(2.0, speaker_worker.diart_latency_seconds_for_quality(50))
        self.assertEqual(5.0, speaker_worker.diart_latency_seconds_for_quality(100))
        self.assertEqual({"tau_active": 0.555, "rho_update": 0.422, "delta_new": 1.517}, speaker_worker.diart_hyper_parameters_for_quality(0))
        self.assertEqual({"tau_active": 0.555, "rho_update": 0.422, "delta_new": 1.517}, speaker_worker.diart_hyper_parameters_for_quality(50))
        self.assertEqual({"tau_active": 0.555, "rho_update": 0.422, "delta_new": 1.517}, speaker_worker.diart_hyper_parameters_for_quality(100))

    def test_diart_quality_presets_increase_duration_for_stability(self) -> None:
        self.assertEqual(5.0, speaker_worker.diart_duration_seconds_for_quality(0))
        self.assertEqual(6.0, speaker_worker.diart_duration_seconds_for_quality(50))
        self.assertEqual(8.0, speaker_worker.diart_duration_seconds_for_quality(100))

    def test_diart_quality_presets_keep_fast_step(self) -> None:
        self.assertEqual(0.5, speaker_worker.DIART_STREAM_CHUNK_SECONDS)

    def test_qwen_diarization_uses_four_second_chunks_even_in_sensitive_mode(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.diarization_enabled = True
        engine.config.diarization_model = "pyannote_community"
        engine.config.stt_quality_preset = 0

        self.assertEqual(speaker_worker.BYTES_PER_SECOND * 4, engine.transcribe_chunk_bytes())

    def test_qwen_sensitive_mode_uses_forced_aligner_words_for_speaker_mapping(self) -> None:
        events = []

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A", "SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 2), None, "SPEAKER_A"
                    yield Segment(2, 4), None, "SPEAKER_B"

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.whisper_model = True
            engine.diarization_pipeline = FakePipeline()
            engine.config.asr_engine = "qwen3_asr_diarization"
            engine.config.stt_quality_preset = 0
            engine.config.diarization_enabled = True
            engine.config.diarization_model = "pyannote_community"
            engine._transcribe_wav_parts = lambda _wav_path: [
                speaker_worker.TimedTextPart("첫 번째", 0, 1500, True),
                speaker_worker.TimedTextPart("두 번째", 2200, 3600, True),
            ]

            engine.transcribe("system", tone_pcm(4), 0)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(2, len(captions))
        self.assertEqual(("speaker_1", "첫 번째"), (captions[0]["speakerId"], captions[0]["text"]))
        self.assertEqual(("speaker_2", "두 번째"), (captions[1]["speakerId"], captions[1]["text"]))

    def test_configure_accepts_asr_engine(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "asrEngine": "qwen3_asr_diarization",
                "sttModel": "qwen3-asr-0.6b",
                "diarizationEnabled": False,
            })
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual("qwen3_asr_diarization", engine.config.asr_engine)
        self.assertIn("engine=qwen3_asr_diarization", events[0]["message"])

    def test_configure_names_default_asr_engine_as_faster_whisper(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "asrEngine": "none",
                "sttModel": "small",
                "diarizationEnabled": False,
            })
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual("faster_whisper", engine.config.asr_engine)
        self.assertIn("engine=faster_whisper", events[0]["message"])

    def test_configure_keeps_whisperlivekit_and_diarization_model_independent(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "asrEngine": "whisperlivekit_sortformer",
                "sttModel": "large-v3-turbo",
                "diarizationModel": "pyannote_community",
                "diarizationEnabled": False,
            })
        finally:
            speaker_worker.emit = original_emit

        message = events[0]["message"]
        self.assertIn("engine=whisperlivekit_sortformer", message)
        self.assertIn("diarization=off", message)
        self.assertIn("diarization_model=pyannote_community", message)
        self.assertNotIn("diarization_model=sortformer", message)

    def test_configure_accepts_whisperx_and_sortformer(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "asrEngine": "whisperx",
                "sttModel": "large-v3-turbo",
                "diarizationModel": "sortformer",
                "diarizationEnabled": True,
            })
        finally:
            speaker_worker.emit = original_emit

        message = events[0]["message"]
        self.assertEqual("whisperx", engine.config.asr_engine)
        self.assertEqual("sortformer", engine.config.diarization_model)
        self.assertIn("engine=whisperx", message)
        self.assertIn("diarization_model=sortformer", message)

    def test_ensure_loaded_uses_whisperx_loader_when_selected(self) -> None:
        events = []
        calls = []
        original_emit = speaker_worker.emit
        original_loader = speaker_worker.load_whisperx_model
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.load_whisperx_model = lambda model, language, device, compute_type, models_dir: calls.append((model, language, device, compute_type, models_dir)) or "whisperx-engine"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "asrEngine": "whisperx",
                "sttModel": "medium",
                "sttLanguages": ["ko"],
                "computeMode": "cpu",
                "diarizationEnabled": False,
            })

            engine.ensure_loaded()
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.load_whisperx_model = original_loader

        self.assertEqual([("medium", "ko", "cpu", "int8", Path("models") / "whisperx")], calls)
        self.assertEqual("whisperx-engine", engine.whisperx_engine)
        self.assertTrue(engine.whisper_model)
        self.assertTrue(any(event["stage"] == "stt_loaded" and "WhisperX" in event["message"] for event in events))

    def test_apply_asr_engine_site_path_uses_env_even_when_pythonpath_is_ignored(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            original_path = list(sys.path)
            original_env = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE")
            try:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = temp_dir
                sys.path = [entry for entry in sys.path if entry != temp_dir]

                speaker_worker.apply_asr_engine_site_path()

                self.assertEqual(temp_dir, sys.path[0])
            finally:
                sys.path = original_path
                if original_env is None:
                    os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE", None)
                else:
                    os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = original_env

    def test_apply_asr_engine_site_path_preloads_base_runtime_modules(self) -> None:
        with tempfile.TemporaryDirectory() as base_dir, tempfile.TemporaryDirectory() as temp_dir:
            original_path = list(sys.path)
            original_env = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE")
            original_modules = speaker_worker._ASR_ENGINE_BASE_PRELOAD_MODULES
            original_import = __builtins__["__import__"] if isinstance(__builtins__, dict) else __builtins__.__import__
            imports = []
            try:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = temp_dir
                sys.path = [base_dir]

                def fake_import(name, *args, **kwargs):
                    if name == "fake_base_runtime":
                        imports.append(name)
                        sys.modules[name] = types.ModuleType(name)
                        return sys.modules[name]
                    return original_import(name, *args, **kwargs)

                if isinstance(__builtins__, dict):
                    __builtins__["__import__"] = fake_import
                else:
                    __builtins__.__import__ = fake_import
                speaker_worker._ASR_ENGINE_BASE_PRELOAD_MODULES = ("fake_base_runtime",)

                speaker_worker.apply_asr_engine_site_path()

                self.assertEqual([temp_dir, base_dir], sys.path)
                self.assertEqual(["fake_base_runtime"], imports)
            finally:
                sys.path = original_path
                sys.modules.pop("fake_base_runtime", None)
                speaker_worker._ASR_ENGINE_BASE_PRELOAD_MODULES = original_modules
                if isinstance(__builtins__, dict):
                    __builtins__["__import__"] = original_import
                else:
                    __builtins__.__import__ = original_import
                if original_env is None:
                    os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE", None)
                else:
                    os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = original_env

    def test_apply_asr_engine_site_path_quarantines_asr_engine_torchvision(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            original_path = list(sys.path)
            original_env = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE")
            site_path = Path(temp_dir)
            package_path = site_path / "torchvision"
            metadata_path = site_path / "torchvision-0.23.0.dist-info"
            package_path.mkdir()
            metadata_path.mkdir()
            try:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = temp_dir

                speaker_worker.apply_asr_engine_site_path()

                self.assertFalse(package_path.exists())
                self.assertFalse(metadata_path.exists())
                self.assertTrue((site_path / "torchvision.live_dialogue_translator_disabled").exists())
                self.assertTrue((site_path / "torchvision-0.23.0.dist-info.live_dialogue_translator_disabled").exists())
            finally:
                sys.path = original_path
                if original_env is None:
                    os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE", None)
                else:
                    os.environ["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = original_env

    def test_load_whisperx_model_applies_import_compatibility_patches(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            calls = []
            fake_whisperx = types.ModuleType("whisperx")

            def load_model(model_name, device, **kwargs):
                calls.append(("load_model", model_name, device, kwargs))
                return object()

            fake_whisperx.load_model = load_model
            original_whisperx = sys.modules.get("whisperx")
            original_suppress = speaker_worker.suppress_torchcodec_warning
            original_torchaudio = speaker_worker.apply_torchaudio_compatibility_shims
            original_speechbrain = speaker_worker.patch_speechbrain_lazy_module_inspection
            try:
                sys.modules["whisperx"] = fake_whisperx
                speaker_worker.suppress_torchcodec_warning = lambda: calls.append(("suppress",))
                speaker_worker.apply_torchaudio_compatibility_shims = lambda: calls.append(("torchaudio",))
                speaker_worker.patch_speechbrain_lazy_module_inspection = lambda: calls.append(("speechbrain",))

                engine = speaker_worker.load_whisperx_model("tiny", "ko", "cpu", "float32", Path(temp_dir))

                self.assertIs(fake_whisperx, engine["module"])
                self.assertEqual(["suppress", "torchaudio", "speechbrain"], [call[0] for call in calls[:3]])
                self.assertEqual("load_model", calls[3][0])
            finally:
                if original_whisperx is None:
                    sys.modules.pop("whisperx", None)
                else:
                    sys.modules["whisperx"] = original_whisperx
                speaker_worker.suppress_torchcodec_warning = original_suppress
                speaker_worker.apply_torchaudio_compatibility_shims = original_torchaudio
                speaker_worker.patch_speechbrain_lazy_module_inspection = original_speechbrain

    def test_qwen_timestamp_result_converts_to_timed_text_parts(self) -> None:
        class Result:
            text = "안녕 하세요"
            time_stamps = [
                {"text": "안녕", "start": 0.0, "end": 0.5},
                {"text": " 하세요", "start": 0.5, "end": 1.2},
            ]

        parts = speaker_worker.timed_text_parts_for_qwen_result(Result(), 2000)

        self.assertEqual(["안녕", " 하세요"], [part.text for part in parts])
        self.assertEqual("안녕 하세요", speaker_worker.join_text_parts(parts))
        self.assertEqual([(0, 500), (500, 1200)], [(part.start_ms, part.end_ms) for part in parts])

    def test_word_timed_parts_insert_spaces_when_words_do_not_include_them(self) -> None:
        parts = [
            speaker_worker.TimedTextPart("야", 0, 100, True),
            speaker_worker.TimedTextPart("왼쪽으로", 100, 300, True),
            speaker_worker.TimedTextPart("가야", 300, 500, True),
            speaker_worker.TimedTextPart("돼", 500, 700, True),
            speaker_worker.TimedTextPart("!", 700, 800, True),
        ]

        self.assertEqual("야 왼쪽으로 가야 돼!", speaker_worker.join_text_parts(parts))

    def test_word_timed_parts_preserve_explicit_spacing(self) -> None:
        parts = [
            speaker_worker.TimedTextPart("안녕", 0, 500, True),
            speaker_worker.TimedTextPart(" 하세요", 500, 1200, True),
        ]

        self.assertEqual("안녕 하세요", speaker_worker.join_text_parts(parts))

    def test_word_timed_parts_do_not_insert_spaces_for_cjk_without_word_spacing(self) -> None:
        parts = [
            speaker_worker.TimedTextPart("你", 0, 100, True),
            speaker_worker.TimedTextPart("好", 100, 200, True),
        ]

        self.assertEqual("你好", speaker_worker.join_text_parts(parts))

    def test_load_qwen_model_uses_sdpa_attention_by_default(self) -> None:
        calls = []

        class FakeModel:
            @staticmethod
            def from_pretrained(model_id, **kwargs):
                calls.append((model_id, kwargs))
                return "qwen"

        fake_qwen = types.ModuleType("qwen_asr")
        fake_qwen.Qwen3ASRModel = FakeModel

        original_qwen = sys.modules.get("qwen_asr")
        original_attention = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_ATTENTION")
        original_snapshot = speaker_worker.ensure_qwen_snapshot
        try:
            sys.modules["qwen_asr"] = fake_qwen
            os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_QWEN_ATTENTION", None)
            speaker_worker.ensure_qwen_snapshot = lambda model_id: Path("models") / model_id.split("/")[-1]

            model = speaker_worker.load_qwen_asr_model("qwen3-asr-0.6b", "cuda")

            self.assertEqual("qwen", model)
            self.assertEqual(str(Path("models") / "Qwen3-ASR-0.6B"), calls[0][0])
            self.assertEqual(str(Path("models") / "Qwen3-ForcedAligner-0.6B"), calls[0][1]["forced_aligner"])
            self.assertTrue(calls[0][1]["local_files_only"])
            self.assertEqual("sdpa", calls[0][1]["attn_implementation"])
            self.assertEqual("sdpa", calls[0][1]["forced_aligner_kwargs"]["attn_implementation"])
            self.assertTrue(calls[0][1]["forced_aligner_kwargs"]["local_files_only"])
        finally:
            speaker_worker.ensure_qwen_snapshot = original_snapshot
            if original_qwen is None:
                sys.modules.pop("qwen_asr", None)
            else:
                sys.modules["qwen_asr"] = original_qwen
            if original_attention is None:
                os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_QWEN_ATTENTION", None)
            else:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_ATTENTION"] = original_attention

    def test_load_qwen_model_can_skip_forced_aligner_for_separated_audio(self) -> None:
        calls = []

        class FakeModel:
            @staticmethod
            def from_pretrained(model_id, **kwargs):
                calls.append((model_id, kwargs))
                return "qwen"

        fake_qwen = types.ModuleType("qwen_asr")
        fake_qwen.Qwen3ASRModel = FakeModel
        original_qwen = sys.modules.get("qwen_asr")
        original_snapshot = speaker_worker.ensure_qwen_snapshot
        try:
            sys.modules["qwen_asr"] = fake_qwen
            speaker_worker.ensure_qwen_snapshot = lambda model_id: Path("models") / model_id.split("/")[-1]

            model = speaker_worker.load_qwen_asr_model("qwen3-asr-0.6b", "cuda", use_aligner=False)
        finally:
            speaker_worker.ensure_qwen_snapshot = original_snapshot
            if original_qwen is None:
                sys.modules.pop("qwen_asr", None)
            else:
                sys.modules["qwen_asr"] = original_qwen

        self.assertEqual("qwen", model)
        self.assertNotIn("forced_aligner", calls[0][1])
        self.assertNotIn("forced_aligner_kwargs", calls[0][1])

    def test_qwen_forced_aligner_items_convert_to_timed_text_parts(self) -> None:
        class Item:
            def __init__(self, text, start_time, end_time):
                self.text = text
                self.start_time = start_time
                self.end_time = end_time

        class Result:
            text = "안녕 하세요"
            time_stamps = [
                Item("안녕", 0.0, 0.5),
                Item(" 하세요", 0.5, 1.2),
            ]

        parts = speaker_worker.timed_text_parts_for_qwen_result(Result(), 2000)

        self.assertEqual(["안녕", " 하세요"], [part.text for part in parts])
        self.assertEqual("안녕 하세요", speaker_worker.join_text_parts(parts))
        self.assertTrue(all(part.has_word_timing for part in parts))
        self.assertEqual([(0, 500), (500, 1200)], [(part.start_ms, part.end_ms) for part in parts])

    def test_qwen_transcribe_requests_punctuation_context(self) -> None:
        transcribe_calls = []

        class Result:
            text = "hello world"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                return [Result()]

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.stt_languages = ["en"]
        engine.qwen_model = FakeQwen()

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))
            engine._transcribe_qwen_wav_parts(wav_path)
        finally:
            wav_path.unlink(missing_ok=True)

        self.assertIn("punctuation", transcribe_calls[0]["context"].lower())
        self.assertIn("sentence boundaries", transcribe_calls[0]["context"].lower())

    def test_qwen_auto_punctuation_preserves_unpunctuated_final_text(self) -> None:
        english = speaker_worker.punctuate_qwen_text_parts([
            speaker_worker.TimedTextPart("hello world", 0, 1000, False),
        ], "English")
        japanese = speaker_worker.punctuate_qwen_text_parts([
            speaker_worker.TimedTextPart("こんにちは世界", 0, 1000, False),
        ], "Japanese")

        self.assertEqual("hello world", speaker_worker.join_text_parts(english))
        self.assertEqual("こんにちは世界", speaker_worker.join_text_parts(japanese))

    def test_qwen_auto_punctuation_marks_long_timed_pauses(self) -> None:
        parts = speaker_worker.punctuate_qwen_text_parts([
            speaker_worker.TimedTextPart("this", 0, 200, True),
            speaker_worker.TimedTextPart("works", 200, 500, True),
            speaker_worker.TimedTextPart("next", 1400, 1600, True),
            speaker_worker.TimedTextPart("sentence", 1600, 2000, True),
        ], "English")

        self.assertEqual("this works. next sentence", speaker_worker.join_text_parts(parts))

    def test_qwen_timestamp_parts_do_not_insert_spaces_before_korean_bound_morphemes(self) -> None:
        class Result:
            text = "기간 또 하나의 요소가 될 수 있지만 여러 요소들이 있잖아. 미안합니다."
            time_stamps = [
                {"text": "기간", "start": 0.0, "end": 0.2},
                {"text": "또", "start": 0.2, "end": 0.4},
                {"text": "하나", "start": 0.4, "end": 0.6},
                {"text": "의", "start": 0.6, "end": 0.7},
                {"text": "요소", "start": 0.7, "end": 0.9},
                {"text": "가", "start": 0.9, "end": 1.0},
                {"text": "될", "start": 1.0, "end": 1.2},
                {"text": "수", "start": 1.2, "end": 1.4},
                {"text": "있지만", "start": 1.4, "end": 1.8},
                {"text": "여러", "start": 1.8, "end": 2.0},
                {"text": "요소", "start": 2.0, "end": 2.2},
                {"text": "들이", "start": 2.2, "end": 2.4},
                {"text": "있잖아", "start": 2.4, "end": 2.8},
                {"text": ".", "start": 2.8, "end": 2.9},
                {"text": "미안", "start": 2.9, "end": 3.1},
                {"text": "합니다", "start": 3.1, "end": 3.6},
                {"text": ".", "start": 3.6, "end": 3.7},
            ]

        parts = speaker_worker.timed_text_parts_for_qwen_result(Result(), 4000)

        self.assertEqual(Result.text, speaker_worker.join_text_parts(parts))

    def test_qwen_stable_mode_attaches_speakers_from_forced_aligner_timestamps(self) -> None:
        events = []
        transcribe_calls = []

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A", "SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 2), None, "SPEAKER_A"
                    yield Segment(2, 4), None, "SPEAKER_B"

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization()

        class Result:
            text = "첫 번째 말 두 번째 말"
            time_stamps = [
                {"text": "첫", "start": 0.0, "end": 0.5},
                {"text": " 번째", "start": 0.5, "end": 1.2},
                {"text": " 말", "start": 1.2, "end": 1.5},
                {"text": " 두", "start": 2.0, "end": 2.4},
                {"text": " 번째", "start": 2.4, "end": 3.0},
                {"text": " 말", "start": 3.0, "end": 3.5},
            ]

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append((audio, kwargs))
                return [Result()]

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.config.asr_engine = "qwen3_asr_diarization"
            engine.config.stt_quality_preset = 100
            engine.config.stt_languages = ["ko"]
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2
            engine.whisper_model = True
            engine.qwen_model = FakeQwen()
            engine.diarization_pipeline = FakePipeline()

            engine.transcribe("system", tone_pcm(4), 0)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(1, len(transcribe_calls))
        self.assertTrue(transcribe_calls[0][1]["return_time_stamps"])
        self.assertEqual("Korean", transcribe_calls[0][1]["language"])
        self.assertEqual(2, len(captions))
        self.assertEqual(("speaker_1", "첫 번째 말"), (captions[0]["speakerId"], captions[0]["text"]))
        self.assertEqual(("speaker_2", "두 번째 말"), (captions[1]["speakerId"], captions[1]["text"]))

    def test_qwen_disables_timestamps_for_languages_unsupported_by_forced_aligner(self) -> None:
        transcribe_calls = []

        class Result:
            text = "ทดสอบ"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                return [Result()]

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.stt_languages = ["th"]
        engine.qwen_model = FakeQwen()

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))
            parts = engine._transcribe_qwen_wav_parts(wav_path)
        finally:
            wav_path.unlink(missing_ok=True)

        self.assertEqual(1, len(parts))
        self.assertEqual("Thai", transcribe_calls[0]["language"])
        self.assertFalse(transcribe_calls[0]["return_time_stamps"])
        self.assertFalse(parts[0].has_word_timing)

    def test_qwen_retries_plain_text_when_forced_aligner_result_is_empty(self) -> None:
        transcribe_calls = []

        class Result:
            def __init__(self, text):
                self.text = text
                self.time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                if kwargs.get("return_time_stamps"):
                    return [Result("")]
                return [Result("다시 나오는 자막")]

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.stt_languages = ["ko"]
        engine.qwen_model = FakeQwen()

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))
            parts = engine._transcribe_qwen_wav_parts(wav_path)
        finally:
            wav_path.unlink(missing_ok=True)

        self.assertEqual([True, False], [call["return_time_stamps"] for call in transcribe_calls])
        self.assertEqual("다시 나오는 자막", speaker_worker.join_text_parts(parts))
        self.assertFalse(parts[0].has_word_timing)

    def test_qwen_uses_faster_whisper_fallback_when_qwen_result_stays_empty(self) -> None:
        transcribe_calls = []

        class QwenResult:
            text = ""
            time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                return [QwenResult()]

        class Segment:
            text = "fallback 자막"
            start = 0.0
            end = 1.0
            words = None

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, *_args, **_kwargs):
                return [Segment()], Info()

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.stt_languages = ["ko"]
        engine.qwen_model = FakeQwen()
        engine.qwen_fallback_whisper_model = FakeWhisper()

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))
            parts = engine._transcribe_qwen_wav_parts(wav_path)
        finally:
            wav_path.unlink(missing_ok=True)

        self.assertEqual([True, False], [call["return_time_stamps"] for call in transcribe_calls])
        self.assertEqual("fallback 자막", speaker_worker.join_text_parts(parts))
        self.assertFalse(parts[0].has_word_timing)

    def test_qwen_timeout_disables_qwen_and_uses_faster_whisper_fallback(self) -> None:
        transcribe_calls = []

        class QwenResult:
            text = "늦은 qwen"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                time.sleep(0.05)
                return [QwenResult()]

        class Segment:
            text = "timeout fallback"
            start = 0.0
            end = 1.0
            words = None

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, *_args, **_kwargs):
                return [Segment()], Info()

        original_timeout = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS")
        os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS"] = "0.01"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.config.asr_engine = "qwen3_asr_diarization"
            engine.config.stt_languages = ["ko"]
            engine.qwen_model = FakeQwen()
            engine.qwen_fallback_whisper_model = FakeWhisper()

            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
                wav_path = Path(temp.name)
            try:
                speaker_worker.write_wav(wav_path, tone_pcm(1))
                parts = engine._transcribe_qwen_wav_parts(wav_path)
            finally:
                wav_path.unlink(missing_ok=True)
        finally:
            if original_timeout is None:
                os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS", None)
            else:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS"] = original_timeout

        self.assertEqual([True], [call["return_time_stamps"] for call in transcribe_calls])
        self.assertEqual("timeout fallback", speaker_worker.join_text_parts(parts))
        self.assertIsNotNone(engine.qwen_disabled_reason)

    def test_qwen_default_timeout_covers_supported_gpu_inference_budget(self) -> None:
        original_timeout = os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS", None)
        try:
            self.assertEqual(8.0, speaker_worker.qwen_timeout_seconds())
        finally:
            if original_timeout is not None:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS"] = original_timeout

    def test_qwen_asr_worker_calls_model_without_nested_timeout_thread(self) -> None:
        qwen_threads = []

        class QwenResult:
            text = "직접 호출"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, *_args, **_kwargs):
                qwen_threads.append(threading.current_thread().name)
                return [QwenResult()]

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.stt_languages = ["ko"]
        engine.qwen_model = FakeQwen()

        def run() -> None:
            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
                wav_path = Path(temp.name)
            try:
                speaker_worker.write_wav(wav_path, tone_pcm(1))
                engine._transcribe_qwen_wav_parts(wav_path)
            finally:
                wav_path.unlink(missing_ok=True)

        thread = threading.Thread(target=run, name="live-dialogue-translator-asr")
        thread.start()
        thread.join(1.0)

        self.assertFalse(thread.is_alive())
        self.assertEqual(["live-dialogue-translator-asr"], qwen_threads)

    def test_qwen_asr_worker_keeps_completed_result_after_timeout_notice(self) -> None:
        class QwenResult:
            text = "완료된 qwen"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, *_args, **_kwargs):
                time.sleep(0.05)
                return [QwenResult()]

        original_timeout = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS")
        os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS"] = "0.01"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.qwen_model = FakeQwen()
            result_box = {}

            def run() -> None:
                result_box["results"] = engine._call_qwen_transcribe_in_asr_worker(Path("audio.wav"), "Korean", False)

            thread = threading.Thread(target=run, name="live-dialogue-translator-asr")
            thread.start()
            thread.join(1.0)
        finally:
            if original_timeout is None:
                os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS", None)
            else:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS"] = original_timeout

        self.assertFalse(thread.is_alive())
        self.assertEqual("완료된 qwen", result_box["results"][0].text)
        self.assertIn("timed out", engine.qwen_disabled_reason or "")

    def test_qwen_slow_completed_result_keeps_using_qwen_for_later_chunks(self) -> None:
        transcribe_calls = []

        class QwenResult:
            text = "느린 qwen"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                time.sleep(0.05)
                return [QwenResult()]

        original_slow = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_SLOW_FALLBACK_SECONDS")
        os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_SLOW_FALLBACK_SECONDS"] = "0.01"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.config.asr_engine = "qwen3_asr_diarization"
            engine.config.stt_languages = ["ko"]
            engine.qwen_model = FakeQwen()

            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
                wav_path = Path(temp.name)
            try:
                speaker_worker.write_wav(wav_path, tone_pcm(1))
                first_parts = engine._transcribe_qwen_wav_parts(wav_path)
                second_parts = engine._transcribe_qwen_wav_parts(wav_path)
            finally:
                wav_path.unlink(missing_ok=True)
        finally:
            if original_slow is None:
                os.environ.pop("LIVE_DIALOGUE_TRANSLATOR_QWEN_SLOW_FALLBACK_SECONDS", None)
            else:
                os.environ["LIVE_DIALOGUE_TRANSLATOR_QWEN_SLOW_FALLBACK_SECONDS"] = original_slow

        self.assertEqual([True, True], [call["return_time_stamps"] for call in transcribe_calls])
        self.assertEqual("느린 qwen", speaker_worker.join_text_parts(first_parts))
        self.assertEqual("느린 qwen", speaker_worker.join_text_parts(second_parts))
        self.assertIsNone(engine.qwen_disabled_reason)

    def test_qwen_separated_audio_does_not_request_timestamp_alignment(self) -> None:
        transcribe_calls = []

        class QwenResult:
            text = "분리 자막"
            time_stamps = []

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append(kwargs)
                return [QwenResult()]

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.speech_separation_model = "mossformer2_ss_16k"
        engine.qwen_model = FakeQwen()

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))
            parts = engine._transcribe_qwen_wav_parts(wav_path)
        finally:
            wav_path.unlink(missing_ok=True)

        self.assertEqual([False], [call["return_time_stamps"] for call in transcribe_calls])
        self.assertEqual("분리 자막", speaker_worker.join_text_parts(parts))

    def test_qwen_fallback_retries_with_relaxed_whisper_options_when_empty(self) -> None:
        whisper_calls = []

        class EmptyInfo:
            language = "ko"

        class Segment:
            text = "relaxed fallback"
            start = 0.0
            end = 1.0
            words = None

        class FakeWhisper:
            def transcribe(self, *_args, **kwargs):
                whisper_calls.append(kwargs)
                if kwargs.get("vad_filter") is False:
                    return [Segment()], EmptyInfo()
                return [], EmptyInfo()

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.stt_languages = ["ko"]
        engine.qwen_fallback_whisper_model = FakeWhisper()

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))
            parts = engine._transcribe_qwen_fallback_whisper_parts(wav_path)
        finally:
            wav_path.unlink(missing_ok=True)

        self.assertEqual(2, len(whisper_calls))
        self.assertTrue(whisper_calls[0]["vad_filter"])
        self.assertFalse(whisper_calls[1]["vad_filter"])
        self.assertEqual("relaxed fallback", speaker_worker.join_text_parts(parts))

    def test_qwen_alignment_debug_reports_timed_parts_once(self) -> None:
        events = []
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine._emit_qwen_alignment_debug(True, [
                speaker_worker.TimedTextPart("안녕", 0, 500, True),
                speaker_worker.TimedTextPart("하세요", 500, 1200, True),
            ])
            engine._emit_qwen_alignment_debug(True, [
                speaker_worker.TimedTextPart("다시", 0, 400, True),
            ])
        finally:
            speaker_worker.emit = original_emit

        self.assertEqual(1, len(events))
        self.assertEqual("qwen_alignment", events[0]["stage"])
        self.assertIn("parts=2", events[0]["message"])
        self.assertIn("span=0-1200 ms", events[0]["message"])

    def test_whisperlivekit_sortformer_transcribe_emits_worker_events(self) -> None:
        events = []

        class FakeWhisperLiveKitSession:
            def __init__(self, _engine):
                pass

            def transcribe_pcm(self, _pcm, base_start_ms, latency_ms):
                return [{
                    "type": "final_caption",
                    "speakerId": "speaker_2",
                    "text": "정상 출력",
                    "startMs": base_start_ms,
                    "endMs": base_start_ms + 1000,
                    "latencyMs": latency_ms,
                }]

            def close(self):
                pass

        original_emit = speaker_worker.emit
        original_session = getattr(speaker_worker, "WhisperLiveKitStreamingSession", None)
        original_wlk = speaker_worker.transcribe_with_whisperlivekit
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.WhisperLiveKitStreamingSession = FakeWhisperLiveKitSession
        speaker_worker.transcribe_with_whisperlivekit = lambda *_args, **_kwargs: self.fail("WhisperLiveKit should use the persistent streaming session")
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.config.asr_engine = "whisperlivekit_sortformer"
            engine.config.diarization_enabled = True
            engine.config.diarization_model = "sortformer"
            engine.whisperlivekit_engine = object()
            engine.whisper_model = True

            engine.transcribe("system", tone_pcm(2), 0)
        finally:
            speaker_worker.emit = original_emit
            if original_session is None:
                delattr(speaker_worker, "WhisperLiveKitStreamingSession")
            else:
                speaker_worker.WhisperLiveKitStreamingSession = original_session
            speaker_worker.transcribe_with_whisperlivekit = original_wlk

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(1, len(captions))
        self.assertEqual("speaker_1", captions[0]["speakerId"])
        self.assertEqual("정상 출력", captions[0]["text"])

    def test_whisperlivekit_sortformer_reuses_streaming_session_between_chunks(self) -> None:
        instances = []

        class FakeWhisperLiveKitSession:
            def __init__(self, engine_obj):
                self.engine_obj = engine_obj
                self.calls = []
                self.closed = False
                instances.append(self)

            def transcribe_pcm(self, pcm, base_start_ms, latency_ms):
                self.calls.append((len(pcm), base_start_ms, latency_ms))
                return []

            def close(self):
                self.closed = True

        original_session = getattr(speaker_worker, "WhisperLiveKitStreamingSession", None)
        original_wlk = speaker_worker.transcribe_with_whisperlivekit
        speaker_worker.WhisperLiveKitStreamingSession = FakeWhisperLiveKitSession
        speaker_worker.transcribe_with_whisperlivekit = lambda *_args, **_kwargs: self.fail("WhisperLiveKit should not recreate one-shot sessions per chunk")
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.config.asr_engine = "whisperlivekit_sortformer"
            engine.config.diarization_enabled = True
            engine.config.diarization_model = "sortformer"
            engine.whisperlivekit_engine = object()
            engine.whisper_model = True

            engine.transcribe("system", tone_pcm(2), 0)
            engine.transcribe("system", tone_pcm(2), 2000)
            engine.close_whisperlivekit_session()
        finally:
            if original_session is None:
                delattr(speaker_worker, "WhisperLiveKitStreamingSession")
            else:
                speaker_worker.WhisperLiveKitStreamingSession = original_session
            speaker_worker.transcribe_with_whisperlivekit = original_wlk

        self.assertEqual(1, len(instances))
        self.assertEqual(2, len(instances[0].calls))
        self.assertTrue(instances[0].closed)

    def test_whisperlivekit_sortformer_uses_streaming_diarization_speaker_for_captions(self) -> None:
        events = []

        class FakeWhisperLiveKitSession:
            def __init__(self, _engine):
                pass

            def transcribe_pcm(self, _pcm, _base_start_ms, latency_ms):
                return [{
                    "type": "final_caption",
                    "speakerId": "speaker_1",
                    "text": "화자 매핑",
                    "startMs": 5200,
                    "endMs": 6600,
                    "latencyMs": latency_ms,
                }]

            def close(self):
                pass

        original_emit = speaker_worker.emit
        original_session = getattr(speaker_worker, "WhisperLiveKitStreamingSession", None)
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.WhisperLiveKitStreamingSession = FakeWhisperLiveKitSession
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.config.asr_engine = "whisperlivekit_sortformer"
            engine.config.diarization_enabled = True
            engine.config.diarization_model = "sortformer"
            engine.whisperlivekit_engine = object()
            engine.whisper_model = True
            engine.streaming_diarization_segments["system"] = [
                (5000, 7000, "speaker_2"),
            ]
            engine.last_diarization_speakers["system"] = "speaker_2"

            engine.transcribe("system", tone_pcm(2), 5000)
        finally:
            speaker_worker.emit = original_emit
            if original_session is None:
                delattr(speaker_worker, "WhisperLiveKitStreamingSession")
            else:
                speaker_worker.WhisperLiveKitStreamingSession = original_session

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(1, len(captions))
        self.assertEqual("speaker_2", captions[0]["speakerId"])

    def test_whisperlivekit_sortformer_uses_external_label_when_overlap_is_not_ready(self) -> None:
        events = []

        class FakeWhisperLiveKitSession:
            def __init__(self, _engine):
                pass

            def transcribe_pcm(self, _pcm, _base_start_ms, latency_ms):
                return [{
                    "type": "final_caption",
                    "speakerId": "speaker_1",
                    "text": "늦은 화자 매핑",
                    "startMs": 7200,
                    "endMs": 7800,
                    "latencyMs": latency_ms,
                }]

            def close(self):
                pass

        original_emit = speaker_worker.emit
        original_session = getattr(speaker_worker, "WhisperLiveKitStreamingSession", None)
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.WhisperLiveKitStreamingSession = FakeWhisperLiveKitSession
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.config.asr_engine = "whisperlivekit_sortformer"
            engine.config.diarization_enabled = True
            engine.config.diarization_model = "sortformer"
            engine.whisperlivekit_engine = object()
            engine.whisper_model = True
            engine.last_diarization_speakers["system"] = "speaker_3"

            engine.transcribe("system", tone_pcm(2), 7000)
        finally:
            speaker_worker.emit = original_emit
            if original_session is None:
                delattr(speaker_worker, "WhisperLiveKitStreamingSession")
            else:
                speaker_worker.WhisperLiveKitStreamingSession = original_session

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(1, len(captions))
        self.assertEqual("speaker_1", captions[0]["speakerId"])

    def test_whisperlivekit_sortformer_failure_does_not_fall_back_to_bool_whisper(self) -> None:
        events = []

        class BrokenWhisperLiveKitSession:
            def __init__(self, _engine):
                pass

            def transcribe_pcm(self, *_args, **_kwargs):
                raise OSError("[WinError 1225] connection refused")

            def close(self):
                pass

        original_emit = speaker_worker.emit
        original_session = getattr(speaker_worker, "WhisperLiveKitStreamingSession", None)
        original_wlk = speaker_worker.transcribe_with_whisperlivekit
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.WhisperLiveKitStreamingSession = BrokenWhisperLiveKitSession
        speaker_worker.transcribe_with_whisperlivekit = lambda *_args, **_kwargs: self.fail("WhisperLiveKit should use the persistent streaming session")
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.config.asr_engine = "whisperlivekit_sortformer"
            engine.whisperlivekit_engine = object()
            engine.whisper_model = True
            engine._transcribe_wav_parts = lambda _wav_path: self.fail("WhisperLiveKit errors must not fall back to the regular Whisper path")

            engine.transcribe("system", tone_pcm(2), 0)
        finally:
            speaker_worker.emit = original_emit
            if original_session is None:
                delattr(speaker_worker, "WhisperLiveKitStreamingSession")
            else:
                speaker_worker.WhisperLiveKitStreamingSession = original_session
            speaker_worker.transcribe_with_whisperlivekit = original_wlk

        errors = [event for event in events if event.get("type") == "error"]
        self.assertEqual(1, len(errors))
        self.assertEqual("whisperlivekit_failed", errors[0]["code"])

    def test_whisperlivekit_transcribe_uses_in_process_audio_processor(self) -> None:
        calls = []

        class FakeAudioProcessor:
            def __init__(self, transcription_engine):
                calls.append(("init", transcription_engine))
                self.queue = None
                self.received = []
                FakeAudioProcessor.latest = self

            async def create_tasks(self):
                import asyncio

                self.queue = asyncio.Queue()

                async def generator():
                    item = await self.queue.get()
                    yield item

                return generator()

            async def process_audio(self, data):
                self.received.append(data)
                if data == b"":
                    await self.queue.put({
                        "lines": [{
                            "speaker": 2,
                            "text": "인프로세스 출력",
                            "start": "0:00:00.00",
                            "end": "0:00:01.00",
                        }],
                    })

            async def cleanup(self):
                calls.append(("cleanup", len(self.received)))

        fake_whisperlivekit = types.ModuleType("whisperlivekit")

        def network_client_should_not_run(*_args, **_kwargs):
            raise OSError("network client should not run")

        fake_whisperlivekit.transcribe_audio = network_client_should_not_run
        fake_audio_processor = types.ModuleType("whisperlivekit.audio_processor")
        fake_audio_processor.AudioProcessor = FakeAudioProcessor

        original_pkg = sys.modules.get("whisperlivekit")
        original_audio_processor = sys.modules.get("whisperlivekit.audio_processor")
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        sys.modules["whisperlivekit.audio_processor"] = fake_audio_processor
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            speaker_worker.write_wav(wav_path, tone_pcm(1))

            events = speaker_worker.transcribe_with_whisperlivekit(object(), wav_path, 2000, 123)
        finally:
            wav_path.unlink(missing_ok=True)
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_audio_processor is None:
                sys.modules.pop("whisperlivekit.audio_processor", None)
            else:
                sys.modules["whisperlivekit.audio_processor"] = original_audio_processor

        self.assertTrue(any(call[0] == "init" for call in calls))
        self.assertTrue(any(call[0] == "cleanup" for call in calls))
        self.assertEqual(1, len(events))
        self.assertEqual("speaker_2", events[0]["speakerId"])
        self.assertEqual("인프로세스 출력", events[0]["text"])
        self.assertEqual(2000, events[0]["startMs"])

    def test_whisperlivekit_streaming_session_keeps_audio_processor_open_between_chunks(self) -> None:
        calls = []

        class FakeAudioProcessor:
            def __init__(self, transcription_engine):
                calls.append(("init", transcription_engine))
                self.queue = None
                self.non_empty_chunks = 0

            async def create_tasks(self):
                import asyncio

                self.queue = asyncio.Queue()

                async def generator():
                    while True:
                        item = await self.queue.get()
                        if item is None:
                            return
                        yield item

                return generator()

            async def process_audio(self, data):
                if data == b"":
                    await self.queue.put(None)
                    return
                self.non_empty_chunks += 1
                index = self.non_empty_chunks
                await self.queue.put({
                    "lines": [{
                        "speaker": 1,
                        "text": f"청크 {index}",
                        "start": float(index),
                        "end": float(index) + 0.5,
                    }],
                })

            async def cleanup(self):
                calls.append(("cleanup", self.non_empty_chunks))

        fake_whisperlivekit = types.ModuleType("whisperlivekit")
        fake_audio_processor = types.ModuleType("whisperlivekit.audio_processor")
        fake_audio_processor.AudioProcessor = FakeAudioProcessor
        original_pkg = sys.modules.get("whisperlivekit")
        original_audio_processor = sys.modules.get("whisperlivekit.audio_processor")
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"),
            "LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"),
        }
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        sys.modules["whisperlivekit.audio_processor"] = fake_audio_processor
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"] = "1"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"] = "0.02"
        try:
            session = speaker_worker.WhisperLiveKitStreamingSession(object())
            try:
                first = session.transcribe_pcm(tone_pcm(1), 1000, 10)
                second = session.transcribe_pcm(tone_pcm(1), 2000, 20)
            finally:
                session.close()
        finally:
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_audio_processor is None:
                sys.modules.pop("whisperlivekit.audio_processor", None)
            else:
                sys.modules["whisperlivekit.audio_processor"] = original_audio_processor
            for key, value in original_env.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertEqual(1, len([call for call in calls if call[0] == "init"]))
        self.assertEqual([("cleanup", 2)], [call for call in calls if call[0] == "cleanup"])
        self.assertEqual(["청크 1"], [event["text"] for event in first])
        self.assertEqual(["청크 2"], [event["text"] for event in second])

    def test_whisperlivekit_streaming_session_suppresses_aggregate_duplicate_lines(self) -> None:
        class FakeAudioProcessor:
            def __init__(self, transcription_engine):
                self.queue = None

            async def create_tasks(self):
                import asyncio

                self.queue = asyncio.Queue()

                async def generator():
                    while True:
                        item = await self.queue.get()
                        if item is None:
                            return
                        yield item

                return generator()

            async def process_audio(self, data):
                if data == b"":
                    await self.queue.put(None)
                    return
                await self.queue.put({
                    "lines": [
                        {"speaker": 1, "text": "첫 문장", "start": 0.0, "end": 1.0},
                        {"speaker": 1, "text": "둘 문장", "start": 1.0, "end": 2.0},
                        {"speaker": 1, "text": "첫 문장 둘 문장", "start": 0.0, "end": 2.0},
                    ],
                })

            async def cleanup(self):
                pass

        fake_whisperlivekit = types.ModuleType("whisperlivekit")
        fake_audio_processor = types.ModuleType("whisperlivekit.audio_processor")
        fake_audio_processor.AudioProcessor = FakeAudioProcessor
        original_pkg = sys.modules.get("whisperlivekit")
        original_audio_processor = sys.modules.get("whisperlivekit.audio_processor")
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"),
            "LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"),
        }
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        sys.modules["whisperlivekit.audio_processor"] = fake_audio_processor
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"] = "1"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"] = "0.02"
        try:
            session = speaker_worker.WhisperLiveKitStreamingSession(object())
            try:
                events = session.transcribe_pcm(tone_pcm(1), 1000, 10)
            finally:
                session.close()
        finally:
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_audio_processor is None:
                sys.modules.pop("whisperlivekit.audio_processor", None)
            else:
                sys.modules["whisperlivekit.audio_processor"] = original_audio_processor
            for key, value in original_env.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertEqual(["첫 문장", "둘 문장"], [event["text"] for event in events])

    def test_whisperlivekit_streaming_session_keeps_intermediate_snapshot_lines(self) -> None:
        class FakeAudioProcessor:
            def __init__(self, transcription_engine):
                self.queue = None

            async def create_tasks(self):
                import asyncio

                self.queue = asyncio.Queue()

                async def generator():
                    while True:
                        item = await self.queue.get()
                        if item is None:
                            return
                        yield item

                return generator()

            async def process_audio(self, data):
                if data == b"":
                    await self.queue.put(None)
                    return
                await self.queue.put({
                    "lines": [
                        {"speaker": 3, "text": "젊음이 좋네요", "start": 1.0, "end": 2.0},
                    ],
                })
                await self.queue.put({
                    "lines": [
                        {"speaker": 3, "text": "진짜 체력", "start": 3.0, "end": 4.0},
                    ],
                })

            async def cleanup(self):
                pass

        fake_whisperlivekit = types.ModuleType("whisperlivekit")
        fake_audio_processor = types.ModuleType("whisperlivekit.audio_processor")
        fake_audio_processor.AudioProcessor = FakeAudioProcessor
        original_pkg = sys.modules.get("whisperlivekit")
        original_audio_processor = sys.modules.get("whisperlivekit.audio_processor")
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"),
            "LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"),
        }
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        sys.modules["whisperlivekit.audio_processor"] = fake_audio_processor
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"] = "1"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"] = "0.02"
        try:
            session = speaker_worker.WhisperLiveKitStreamingSession(object())
            try:
                events = session.transcribe_pcm(tone_pcm(1), 1000, 10)
            finally:
                session.close()
        finally:
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_audio_processor is None:
                sys.modules.pop("whisperlivekit.audio_processor", None)
            else:
                sys.modules["whisperlivekit.audio_processor"] = original_audio_processor
            for key, value in original_env.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertEqual(["젊음이 좋네요", "진짜 체력"], [event["text"] for event in events])

    def test_whisperlivekit_streaming_session_suppresses_revision_duplicate_lines(self) -> None:
        class FakeAudioProcessor:
            def __init__(self, transcription_engine):
                self.queue = None

            async def create_tasks(self):
                import asyncio

                self.queue = asyncio.Queue()

                async def generator():
                    while True:
                        item = await self.queue.get()
                        if item is None:
                            return
                        yield item

                return generator()

            async def process_audio(self, data):
                if data == b"":
                    await self.queue.put(None)
                    return
                await self.queue.put({
                    "lines": [{
                        "speaker": 1,
                        "text": "도박군으로 커버칠게요 이거는.",
                        "start": 32.664,
                        "end": 35.931,
                    }],
                })
                await self.queue.put({
                    "lines": [{
                        "speaker": 2,
                        "text": "도박군으로 커버칠게요 이거는.",
                        "start": 33.911,
                        "end": 36.472,
                    }],
                })
                await self.queue.put({
                    "lines": [{
                        "speaker": 2,
                        "text": "도박군으로 커버칠게요 이거는.",
                        "start": 33.911,
                        "end": 36.655,
                    }],
                })
                await self.queue.put({
                    "lines": [{
                        "speaker": 1,
                        "text": "악마사냥꾼이 줄어들어서.",
                        "start": 36.472,
                        "end": 44.341,
                    }],
                })
                await self.queue.put({
                    "lines": [{
                        "speaker": 3,
                        "text": "악마사냥꾼이 줄어들어서.",
                        "start": 36.655,
                        "end": 45.279,
                    }],
                })

            async def cleanup(self):
                pass

        fake_whisperlivekit = types.ModuleType("whisperlivekit")
        fake_audio_processor = types.ModuleType("whisperlivekit.audio_processor")
        fake_audio_processor.AudioProcessor = FakeAudioProcessor
        original_pkg = sys.modules.get("whisperlivekit")
        original_audio_processor = sys.modules.get("whisperlivekit.audio_processor")
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"),
            "LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"),
        }
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        sys.modules["whisperlivekit.audio_processor"] = fake_audio_processor
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"] = "1"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"] = "0.02"
        try:
            session = speaker_worker.WhisperLiveKitStreamingSession(object())
            try:
                events = session.transcribe_pcm(tone_pcm(1), 0, 10)
            finally:
                session.close()
        finally:
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_audio_processor is None:
                sys.modules.pop("whisperlivekit.audio_processor", None)
            else:
                sys.modules["whisperlivekit.audio_processor"] = original_audio_processor
            for key, value in original_env.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertEqual(
            ["도박군으로 커버칠게요 이거는.", "악마사냥꾼이 줄어들어서."],
            [event["text"] for event in events],
        )
        self.assertEqual(["speaker_1", "speaker_1"], [event["speakerId"] for event in events])

    def test_whisperlivekit_streaming_session_drops_stale_backfill_when_result_catches_up(self) -> None:
        first_text = "근데 난 이걸 통해 한 번 더 배웠어 뭘 배웠냐면은 이 지랄 백날 해봐야"

        class FakeAudioProcessor:
            def __init__(self, transcription_engine):
                self.queue = None
                self.non_empty_chunks = 0

            async def create_tasks(self):
                import asyncio

                self.queue = asyncio.Queue()

                async def generator():
                    while True:
                        item = await self.queue.get()
                        if item is None:
                            return
                        yield item

                return generator()

            async def process_audio(self, data):
                if data == b"":
                    await self.queue.put(None)
                    return
                self.non_empty_chunks += 1
                if self.non_empty_chunks == 1:
                    await self.queue.put({
                        "lines": [{
                            "speaker": 1,
                            "text": first_text,
                            "start": 85.209,
                            "end": 94.328,
                        }],
                    })
                    return
                await self.queue.put({
                    "lines": [
                        {
                            "speaker": 1,
                            "text": f"{first_text} 그냥 치명타 극한으로 올린 쪽이 더 세다",
                            "start": 81.480,
                            "end": 106.168,
                        },
                        {
                            "speaker": 1,
                            "text": "지금은 빡겜해야 돼",
                            "start": 109.840,
                            "end": 116.617,
                        },
                    ],
                })

            async def cleanup(self):
                pass

        fake_whisperlivekit = types.ModuleType("whisperlivekit")
        fake_audio_processor = types.ModuleType("whisperlivekit.audio_processor")
        fake_audio_processor.AudioProcessor = FakeAudioProcessor
        original_pkg = sys.modules.get("whisperlivekit")
        original_audio_processor = sys.modules.get("whisperlivekit.audio_processor")
        original_env = {
            "LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"),
            "LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"),
        }
        sys.modules["whisperlivekit"] = fake_whisperlivekit
        sys.modules["whisperlivekit.audio_processor"] = fake_audio_processor
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS"] = "1"
        os.environ["LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS"] = "0.02"
        try:
            session = speaker_worker.WhisperLiveKitStreamingSession(object())
            try:
                first = session.transcribe_pcm(tone_pcm(1), 0, 10)
                second = session.transcribe_pcm(tone_pcm(1), 115943, 20)
            finally:
                session.close()
        finally:
            if original_pkg is None:
                sys.modules.pop("whisperlivekit", None)
            else:
                sys.modules["whisperlivekit"] = original_pkg
            if original_audio_processor is None:
                sys.modules.pop("whisperlivekit.audio_processor", None)
            else:
                sys.modules["whisperlivekit.audio_processor"] = original_audio_processor
            for key, value in original_env.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        self.assertEqual([first_text], [event["text"] for event in first])
        self.assertEqual(["지금은 빡겜해야 돼"], [event["text"] for event in second])

    def test_whisperlivekit_worker_events_split_long_lines_before_emit(self) -> None:
        result = {
            "lines": [{
                "speaker": 1,
                "text": "첫 번째 문장입니다. 두 번째 문장입니다. 세 번째 문장입니다. 네 번째 문장입니다.",
                "start": 10.0,
                "end": 22.0,
            }],
        }

        events = speaker_worker.worker_events_for_whisperlivekit_result(result, 1000, 30)

        self.assertGreater(len(events), 1)
        self.assertEqual("첫 번째 문장입니다.", events[0]["text"])
        self.assertEqual("네 번째 문장입니다.", events[-1]["text"])
        self.assertEqual(11000, events[0]["startMs"])
        self.assertEqual(23000, events[-1]["endMs"])
        for previous, current in zip(events, events[1:]):
            self.assertLessEqual(previous["endMs"], current["startMs"])

    def test_whisperlivekit_worker_events_split_unpunctuated_long_lines(self) -> None:
        long_text = " ".join(f"단어{i}" for i in range(1, 41))
        result = {
            "lines": [{
                "speaker": 1,
                "text": long_text,
                "start": 0.0,
                "end": 20.0,
            }],
        }

        events = speaker_worker.worker_events_for_whisperlivekit_result(result, 0, 30)

        self.assertGreater(len(events), 1)
        self.assertTrue(all(len(event["text"]) <= speaker_worker.WHISPERLIVEKIT_MAX_CAPTION_CHARS for event in events))
        self.assertEqual(long_text.replace(" ", ""), "".join(event["text"].replace(" ", "") for event in events))

    def test_diarization_context_buffer_uses_quality_preset(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.stt_quality_preset = 100

        pcm = b"\1" * speaker_worker.BYTES_PER_SECOND * 140

        context = engine._diarization_context_for("system", pcm)

        self.assertEqual(speaker_worker.BYTES_PER_SECOND * 120, len(context))

    def test_diart_uses_current_audio_window_without_context_replay(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "diart"

        pcm = b"\1" * speaker_worker.BYTES_PER_SECOND * 4
        context = engine._diarization_window_for("system", pcm)

        self.assertEqual(pcm, context)
        self.assertNotIn("system", engine.diarization_buffers)

    def test_accuracy_preset_uses_float16_on_cuda(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.stt_quality_preset = 90

        self.assertEqual("float16", engine._resolve_stt_compute_type("cuda"))

        engine.config.stt_quality_preset = 30

        self.assertEqual("int8_float16", engine._resolve_stt_compute_type("cuda"))

    def test_transcribe_forces_single_selected_language(self) -> None:
        calls = []

        class Segment:
            text = " 안녕하세요"

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **kwargs):
                calls.append(kwargs)
                return [Segment()], Info()

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.whisper_model = FakeWhisper()
        engine.config.stt_languages = ["ko"]
        engine.config.stt_quality_preset = 100

        text = engine._transcribe_wav(Path("audio.wav"))

        self.assertEqual("안녕하세요", text)
        self.assertEqual("ko", calls[0]["language"])
        self.assertFalse(calls[0]["condition_on_previous_text"])
        self.assertEqual(0.0, calls[0]["temperature"])
        self.assertEqual(0.6, calls[0]["no_speech_threshold"])
        self.assertEqual(-1.0, calls[0]["log_prob_threshold"])
        self.assertEqual(2.4, calls[0]["compression_ratio_threshold"])
        self.assertEqual(5, calls[0]["beam_size"])
        self.assertEqual(3, calls[0]["no_repeat_ngram_size"])
        self.assertTrue(calls[0]["word_timestamps"])
        self.assertEqual(500, calls[0]["vad_parameters"]["min_silence_duration_ms"])

    def test_transcribe_retries_primary_language_when_detected_language_is_not_allowed(self) -> None:
        calls = []

        class Segment:
            def __init__(self, text):
                self.text = text

        class Info:
            def __init__(self, language):
                self.language = language

        class FakeWhisper:
            def transcribe(self, path, **kwargs):
                calls.append(kwargs)
                if kwargs.get("language") is None:
                    return [Segment("bonjour")], Info("fr")
                return [Segment("안녕하세요")], Info("ko")

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.whisper_model = FakeWhisper()
        engine.config.stt_languages = ["ko", "en"]

        text = engine._transcribe_wav(Path("audio.wav"))

        self.assertEqual("안녕하세요", text)
        self.assertIsNone(calls[0]["language"])
        self.assertEqual("ko", calls[1]["language"])

    def test_join_segments_text_filters_probable_silence_hallucinations(self) -> None:
        class Segment:
            def __init__(self, text, no_speech_prob, avg_logprob, compression_ratio):
                self.text = text
                self.no_speech_prob = no_speech_prob
                self.avg_logprob = avg_logprob
                self.compression_ratio = compression_ratio

        text = speaker_worker.join_segments_text([
            Segment("시청해 주셔서 감사합니다.", 0.92, -1.25, 1.1),
            Segment("실제 발화입니다.", 0.05, -0.25, 1.0),
            Segment("하하하 하하하 하하하 하하하 하하하", 0.05, -0.20, 3.0),
        ])

        self.assertEqual("실제 발화입니다.", text)

    def test_join_segments_text_filters_common_outro_hallucinations_without_scores(self) -> None:
        class Segment:
            def __init__(self, text):
                self.text = text

        text = speaker_worker.join_segments_text([
            Segment("시청해주셔서 감사합니다. MBC 뉴스입니다."),
            Segment("실제 발화입니다."),
        ])

        self.assertEqual("실제 발화입니다.", text)

    def test_join_text_parts_filters_common_outro_hallucination_words(self) -> None:
        parts = [
            speaker_worker.TimedTextPart("시청해", 0, 200, True),
            speaker_worker.TimedTextPart("주셔서", 200, 400, True),
            speaker_worker.TimedTextPart("감사합니다", 400, 900, True),
        ]

        self.assertEqual("", speaker_worker.join_text_parts(parts))

    def test_hallucination_filter_blocks_known_broadcast_outros(self) -> None:
        hallucinations = [
            "한글자막 by 박진희",
            "지금까지 뉴스 스토리였습니다.",
            "MBC 뉴스 김성현 입니다.",
            "시청해주셔서 감사합니다.",
            "ご視聴ありがとうございました",
            "見てくれてありがとう",
            "Thank you for watching.",
            "Thanks for watching, please subscribe.",
            "Subtitles by the Amara.org community.",
            "Gracias por ver el video.",
            "Obrigado por assistir.",
            "Спасибо за просмотр.",
            "شكرا على المشاهدة",
            "Terima kasih telah menonton.",
            "Merci d'avoir regardé cette vidéo.",
            "谢谢观看 下集再见",
            "Kiitos kun katsoit.",
            "Grazie per la visione.",
        ]

        for text in hallucinations:
            with self.subTest(text=text):
                self.assertEqual("", speaker_worker.filter_transcribed_caption_text(text))

    def test_hallucination_filter_keeps_plain_thanks(self) -> None:
        text = speaker_worker.filter_transcribed_caption_text("감사합니다.")
        english = speaker_worker.filter_transcribed_caption_text("Thank you.")

        self.assertEqual("감사합니다.", text)
        self.assertEqual("Thank you.", english)

    def test_hallucination_filter_keeps_real_mbc_context(self) -> None:
        text = speaker_worker.filter_transcribed_caption_text("MBC 뉴스 보도에 대한 이야기를 이어갑니다.")

        self.assertEqual("MBC 뉴스 보도에 대한 이야기를 이어갑니다.", text)

    def test_configure_resets_loaded_models_when_model_or_compute_changes(self) -> None:
        events = []
        original_emit = speaker_worker.emit
        speaker_worker.emit = lambda event: events.append(event)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({"type": "configure", "sttModel": "tiny", "computeMode": "auto", "diarizationEnabled": True})
            engine.whisper_model = object()
            engine.diarization_pipeline = object()

            engine.configure({"type": "configure", "sttModel": "medium", "computeMode": "cuda", "diarizationEnabled": True})
        finally:
            speaker_worker.emit = original_emit

        self.assertIsNone(engine.whisper_model)
        self.assertIsNone(engine.diarization_pipeline)
        self.assertIn("medium", events[-1]["message"])
        self.assertIn("cuda", events[-1]["message"])

    def test_speaker_for_sends_preloaded_waveform_to_pyannote(self) -> None:
        calls = []

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_00"]

        class FakePipeline:
            def __call__(self, audio, **kwargs):
                calls.append((audio, kwargs))
                return FakeDiarization()

        original = speaker_worker.pcm_to_waveform
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.diarization_pipeline = FakePipeline()
            engine.config.max_speakers = 3

            speaker = engine._speaker_for("system", Path("ignored.wav"), b"\0" * speaker_worker.DIARIZATION_MIN_CONTEXT_BYTES)
        finally:
            speaker_worker.pcm_to_waveform = original

        self.assertEqual("speaker_1", speaker)
        self.assertEqual([({"waveform": "waveform", "sample_rate": speaker_worker.SAMPLE_RATE}, {"min_speakers": 1, "max_speakers": 3})], calls)

    def test_speaker_for_uses_exact_speaker_count_as_streaming_cap(self) -> None:
        calls = []

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_00"]

        class FakePipeline:
            def __call__(self, audio, **kwargs):
                calls.append(kwargs)
                return FakeDiarization()

        original = speaker_worker.pcm_to_waveform
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.diarization_pipeline = FakePipeline()
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2

            engine._speaker_for("system", Path("ignored.wav"), b"\0" * speaker_worker.DIARIZATION_MIN_CONTEXT_BYTES)
        finally:
            speaker_worker.pcm_to_waveform = original

        self.assertEqual([{"min_speakers": 2, "max_speakers": 2}], calls)

    def test_speaker_for_keeps_exact_speaker_count_as_range_after_context_warmup(self) -> None:
        calls = []

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_00"]

        class FakePipeline:
            def __call__(self, audio, **kwargs):
                calls.append(kwargs)
                return FakeDiarization()

        original = speaker_worker.pcm_to_waveform
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.diarization_pipeline = FakePipeline()
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2

            engine._speaker_for("system", Path("ignored.wav"), b"\0" * speaker_worker.BYTES_PER_SECOND * 8)
        finally:
            speaker_worker.pcm_to_waveform = original

        self.assertEqual([{"min_speakers": 2, "max_speakers": 2}], calls)

    def test_exact_speakers_passes_fixed_min_and_max_to_pyannote(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.exact_speakers = 3
        engine.config.max_speakers = 8
        engine.config.diarization_model = "pyannote_community"

        self.assertEqual({"min_speakers": 3, "max_speakers": 3}, engine._diarization_speaker_kwargs(30.0))

    def test_transcribe_uses_rolling_audio_context_for_diarization(self) -> None:
        calls = []

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_00"]

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                calls.append(audio["waveform"])
                return FakeDiarization()

        original_emit = speaker_worker.emit
        original = speaker_worker.pcm_to_waveform
        speaker_worker.emit = lambda _event: None
        speaker_worker.pcm_to_waveform = lambda pcm: len(pcm)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.diarization_pipeline = FakePipeline()
            engine.ensure_loaded = lambda: None
            engine._transcribe_wav_parts = lambda _wav_path: [speaker_worker.TimedTextPart("text", 0, 4000, False)]

            engine.transcribe("system", tone_pcm(4), 0)
            engine.transcribe("system", tone_pcm(3), 4000)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original

        self.assertEqual(speaker_worker.BYTES_PER_SECOND * 4, calls[0])
        self.assertGreater(calls[1], calls[0])

    def test_balanced_transcribe_splits_word_timestamps_by_diarized_turns(self) -> None:
        events = []
        transcribe_calls = []

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A", "SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 2), None, "SPEAKER_A"
                    yield Segment(2, 4), None, "SPEAKER_B"

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization()

        class Word:
            def __init__(self, word, start, end):
                self.word = word
                self.start = start
                self.end = end

        class WhisperSegment:
            text = "첫 번째 말 두 번째 말"
            words = [
                Word("첫", 0.0, 0.5),
                Word(" 번째", 0.5, 1.2),
                Word(" 말", 1.2, 1.5),
                Word(" 두", 2.0, 2.4),
                Word(" 번째", 2.4, 3.0),
                Word(" 말", 3.0, 3.5),
            ]

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **_kwargs):
                transcribe_calls.append(path)
                return [WhisperSegment()], Info()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.whisper_model = FakeWhisper()
            engine.diarization_pipeline = FakePipeline()
            engine.config.stt_quality_preset = 50
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2

            engine.transcribe("system", tone_pcm(4), 0)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(2, len(captions))
        self.assertEqual("speaker_1", captions[0]["speakerId"])
        self.assertEqual("첫 번째 말", captions[0]["text"])
        self.assertEqual(0, captions[0]["startMs"])
        self.assertEqual(1500, captions[0]["endMs"])
        self.assertEqual("speaker_2", captions[1]["speakerId"])
        self.assertEqual("두 번째 말", captions[1]["text"])
        self.assertEqual(2000, captions[1]["startMs"])
        self.assertEqual(3500, captions[1]["endMs"])
        self.assertEqual(1, len(transcribe_calls))
        speaker_segments = [event for event in events if event.get("type") == "speaker_segment"]
        self.assertEqual(2, len(speaker_segments))
        self.assertEqual(("speaker_1", 0, 1650), (speaker_segments[0]["speakerId"], speaker_segments[0]["startMs"], speaker_segments[0]["endMs"]))
        self.assertEqual(("speaker_2", 1650, 4000), (speaker_segments[1]["speakerId"], speaker_segments[1]["startMs"], speaker_segments[1]["endMs"]))

    def test_stable_transcribe_runs_asr_per_diarized_turn_before_word_mapping(self) -> None:
        events = []
        transcribe_calls = []

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A", "SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 2), None, "SPEAKER_A"
                    yield Segment(2, 4), None, "SPEAKER_B"

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization()

        class WhisperSegment:
            def __init__(self, text):
                self.text = text

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **_kwargs):
                transcribe_calls.append(path)
                return [WhisperSegment("첫 번째 말" if len(transcribe_calls) == 1 else "두 번째 말")], Info()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.ensure_loaded = lambda: None
            engine.whisper_model = FakeWhisper()
            engine.diarization_pipeline = FakePipeline()
            engine.config.stt_quality_preset = 100
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2

            engine.transcribe("system", tone_pcm(4), 0)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(2, len(captions))
        self.assertEqual("speaker_1", captions[0]["speakerId"])
        self.assertEqual("첫 번째 말", captions[0]["text"])
        self.assertEqual("speaker_2", captions[1]["speakerId"])
        self.assertEqual("두 번째 말", captions[1]["text"])
        self.assertEqual(2, len(transcribe_calls))

    def test_diart_streaming_keeps_stt_on_whole_chunk(self) -> None:
        events = []
        transcribe_calls = []

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 1), None, "SPEAKER_A"

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization()

        class WhisperSegment:
            text = "전체 청크"

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **_kwargs):
                transcribe_calls.append(path)
                return [WhisperSegment()], Info()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine.ensure_loaded = lambda: None
            engine.whisper_model = FakeWhisper()
            engine.diarization_pipeline = FakePipeline()
            engine.config.diarization_model = "diart"
            engine.config.stt_quality_preset = 50
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2

            engine.transcribe("system", tone_pcm(4), 0)
            self.wait_for_streaming_diarization(engine)
        finally:
            self.wait_for_streaming_diarization(engine)
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(1, len(captions))
        self.assertEqual("전체 청크", captions[0]["text"])
        self.assertEqual(1, len(transcribe_calls))

    def test_diart_accuracy_uses_diarization_as_label_not_stt_slice(self) -> None:
        events = []
        transcribe_calls = []

        class WhisperSegment:
            text = "전체 문장을 유지"

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **_kwargs):
                transcribe_calls.append(path)
                return [WhisperSegment()], Info()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine.ensure_loaded = lambda: None
            engine.whisper_model = FakeWhisper()
            engine.config.diarization_model = "diart"
            engine.config.stt_quality_preset = 100
            engine.config.exact_speakers = 4
            engine.config.max_speakers = 4
            engine.streaming_diarization_segments["system"] = [(0, 4000, "speaker_2")]
            engine.last_diarization_speakers["system"] = "speaker_2"

            engine.transcribe("system", tone_pcm(4), 0, queue_diarization=False)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        speaker_segments = [event for event in events if event.get("type") == "speaker_segment"]
        self.assertEqual(1, len(captions))
        self.assertEqual("전체 문장을 유지", captions[0]["text"])
        self.assertEqual(0, captions[0]["startMs"])
        self.assertEqual(4000, captions[0]["endMs"])
        self.assertEqual("speaker_2", captions[0]["speakerId"])
        self.assertEqual(1, len(transcribe_calls))
        self.assertEqual([], speaker_segments)

    def test_diart_accuracy_splits_word_timestamps_at_speaker_change(self) -> None:
        events = []
        transcribe_calls = []

        class Word:
            def __init__(self, word, start, end):
                self.word = word
                self.start = start
                self.end = end

        class WhisperSegment:
            text = "이전 발화 다음 시작"
            words = [
                Word("이전", 0.0, 1.0),
                Word(" 발화", 1.0, 1.9),
                Word(" 다음", 2.1, 2.7),
                Word(" 시작", 2.7, 3.5),
            ]

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **_kwargs):
                transcribe_calls.append(path)
                return [WhisperSegment()], Info()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine.ensure_loaded = lambda: None
            engine.whisper_model = FakeWhisper()
            engine.config.diarization_model = "diart"
            engine.config.stt_quality_preset = 100
            engine.config.exact_speakers = 2
            engine.config.max_speakers = 2
            engine.streaming_diarization_segments["system"] = [
                (0, 2000, "speaker_1"),
                (2000, 4000, "speaker_2"),
            ]
            engine.last_diarization_speakers["system"] = "speaker_2"

            engine.transcribe("system", tone_pcm(4), 0, queue_diarization=False)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual(2, len(captions))
        self.assertEqual(("speaker_1", "이전 발화"), (captions[0]["speakerId"], captions[0]["text"]))
        self.assertEqual(0, captions[0]["startMs"])
        self.assertLessEqual(captions[0]["endMs"], 2000)
        self.assertEqual(("speaker_2", "다음 시작"), (captions[1]["speakerId"], captions[1]["text"]))
        self.assertGreaterEqual(captions[1]["startMs"], 2000)
        self.assertEqual(1, len(transcribe_calls))

    def test_diart_adapter_feeds_five_second_windows_every_half_second(self) -> None:
        import numpy as np

        starts = []

        class FakePipeline:
            def __call__(self, chunks):
                starts.extend(round(chunk.extent.start, 3) for chunk in chunks)
                return []

            def reset(self):
                pass

        adapter = speaker_worker.DiartPipelineAdapter(FakePipeline(), duration=5.0, step=0.5)
        half_second = np.zeros(int(speaker_worker.SAMPLE_RATE * 0.5), dtype=np.float32)

        for _index in range(9):
            adapter({"waveform": half_second, "sample_rate": speaker_worker.SAMPLE_RATE})

        self.assertEqual([], starts)

        adapter({"waveform": half_second, "sample_rate": speaker_worker.SAMPLE_RATE})
        adapter({"waveform": half_second, "sample_rate": speaker_worker.SAMPLE_RATE})

        self.assertEqual([0.0, 0.5], starts)

    def test_exclusive_diarization_turns_collapses_duplicate_overlap_labels(self) -> None:
        turns = [
            ("SPEAKER_A", 1.0, 1.5),
            ("SPEAKER_B", 1.0, 1.5),
            ("SPEAKER_B", 2.0, 2.7),
        ]

        exclusive = speaker_worker.exclusive_diarization_turns(turns, preferred_label="SPEAKER_B")

        self.assertEqual([
            ("SPEAKER_B", 1.0, 1.5),
            ("SPEAKER_B", 2.0, 2.7),
        ], exclusive)

    def test_speaker_handoff_adjusted_turns_assigns_switch_preroll_to_next_speaker(self) -> None:
        turns = [
            ("SPEAKER_A", 0.0, 2.0),
            ("SPEAKER_B", 2.0, 5.0),
        ]

        adjusted = speaker_worker.speaker_handoff_adjusted_turns(turns)

        self.assertEqual([
            ("SPEAKER_A", 0.0, 1.65),
            ("SPEAKER_B", 1.65, 5.0),
        ], adjusted)

    def test_speaker_handoff_adjusted_turns_keeps_real_gap_between_speakers(self) -> None:
        turns = [
            ("SPEAKER_A", 0.0, 1.0),
            ("SPEAKER_B", 2.0, 5.0),
        ]

        adjusted = speaker_worker.speaker_handoff_adjusted_turns(turns)

        self.assertEqual(turns, adjusted)

    def test_diarization_turn_confidence_uses_duration_and_context(self) -> None:
        short_confidence = speaker_worker.diarization_turn_confidence(0.0, 0.5, 1.0)
        stable_confidence = speaker_worker.diarization_turn_confidence(0.0, 3.0, 8.0)

        self.assertGreater(short_confidence, 0.0)
        self.assertLess(short_confidence, stable_confidence)
        self.assertEqual(0.95, stable_confidence)

    def test_diart_streaming_diarization_does_not_block_stt_caption(self) -> None:
        events = []
        release_diarization = threading.Event()

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                release_diarization.wait(1.0)

                class FakeDiarization:
                    def labels(self):
                        return []

                return FakeDiarization()

        class WhisperSegment:
            text = "바로 표시"

        class Info:
            language = "ko"

        class FakeWhisper:
            def transcribe(self, path, **_kwargs):
                return [WhisperSegment()], Info()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: "waveform"
        try:
            engine.ensure_loaded = lambda: None
            engine.whisper_model = FakeWhisper()
            engine.diarization_pipeline = FakePipeline()
            engine.config.diarization_model = "diart"
            engine.config.stt_quality_preset = 50

            started = time.perf_counter()
            engine.transcribe("system", tone_pcm(4), 0)
            elapsed = time.perf_counter() - started
            captions = [event for event in events if event.get("type") == "final_caption"]
            self.assertEqual("바로 표시", captions[0]["text"])
            self.assertLess(elapsed, 0.5)
        finally:
            release_diarization.set()
            self.wait_for_streaming_diarization(engine)
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

    def test_diart_streaming_diarization_keeps_pending_audio_instead_of_dropping(self) -> None:
        calls = []
        first_call_started = threading.Event()
        release_first_call = threading.Event()

        class FakeDiarization:
            def labels(self):
                return []

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                calls.append(audio["waveform"])
                if len(calls) == 1:
                    first_call_started.set()
                    release_first_call.wait(1.0)
                return FakeDiarization()

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        speaker_worker.emit = lambda _event: None
        speaker_worker.pcm_to_waveform = lambda pcm: len(pcm)
        try:
            engine.diarization_pipeline = FakePipeline()
            engine.config.diarization_model = "diart"

            engine.queue_streaming_diarization("system", tone_pcm(1))
            self.assertTrue(first_call_started.wait(1.0))
            engine.queue_streaming_diarization("system", tone_pcm(1))
            engine.queue_streaming_diarization("system", tone_pcm(1))
            release_first_call.set()
            self.wait_for_streaming_diarization(engine)
        finally:
            release_first_call.set()
            self.wait_for_streaming_diarization(engine)
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        self.assertEqual(2, len(calls))
        self.assertEqual(speaker_worker.BYTES_PER_SECOND, calls[0])
        self.assertEqual(speaker_worker.BYTES_PER_SECOND * 2, calls[1])

    def test_append_streaming_segment_merges_and_prunes_cached_turns(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))

        engine.append_streaming_segment("system", 0, 1000, "speaker_1")
        engine.append_streaming_segment("system", 1100, 2000, "speaker_1")
        engine.append_streaming_segment("system", 2400, 3000, "speaker_1")
        engine.append_streaming_segment("system", 602_000, 603_001, "speaker_2")

        self.assertEqual([(602_000, 603_001, "speaker_2")], engine.streaming_diarization_segments["system"])

        for index in range(405):
            start_ms = 604_000 + (index * 1_000)
            engine.append_streaming_segment("system", start_ms, start_ms + 500, f"speaker_{index % 2}")

        self.assertLessEqual(len(engine.streaming_diarization_segments["system"]), 400)

    def test_select_current_speaker_label_ignores_subsecond_tail_blips(self) -> None:
        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A", "SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 4.7), None, "SPEAKER_A"
                    yield Segment(4.7, 7), None, "SPEAKER_B"

        label = speaker_worker.select_current_speaker_label(
            FakeDiarization(),
            context_seconds=5,
            current_seconds=1,
        )

        self.assertEqual("SPEAKER_A", label)

    def test_select_current_speaker_label_switches_after_sustained_tail(self) -> None:
        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_A", "SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 4), None, "SPEAKER_A"
                    yield Segment(4, 7), None, "SPEAKER_B"

        label = speaker_worker.select_current_speaker_label(
            FakeDiarization(),
            context_seconds=7,
            current_seconds=1,
        )

        self.assertEqual("SPEAKER_B", label)

    def test_select_current_speaker_label_rejects_tiny_overlap(self) -> None:
        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def labels(self):
                return ["SPEAKER_B"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(4.8, 5.0), None, "SPEAKER_B"

        label = speaker_worker.select_current_speaker_label(
            FakeDiarization(),
            context_seconds=5,
            current_seconds=1,
        )

        self.assertIsNone(label)

    def test_speaker_for_keeps_previous_stable_speaker_when_diarization_is_uncertain(self) -> None:
        class FakeDiarization:
            def labels(self):
                return []

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization()

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.diarization_pipeline = FakePipeline()
        engine.stable_segment_speakers["system"] = "speaker_2"

        speaker = engine._speaker_for("system", Path("ignored.wav"), b"\0\0")

        self.assertEqual("speaker_2", speaker)

    def test_transcribe_uses_speaker_dominant_in_stable_tail(self) -> None:
        events = []

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeDiarization:
            def __init__(self, labels):
                self._labels = labels

            def labels(self):
                return list(dict.fromkeys(self._labels))

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 4), None, "SPEAKER_A"
                    yield Segment(4, 5), None, "SPEAKER_B"

        class FakePipeline:
            def __call__(self, audio, **_kwargs):
                return FakeDiarization(["SPEAKER_A", "SPEAKER_B"])

        original_emit = speaker_worker.emit
        original_waveform = speaker_worker.pcm_to_waveform
        speaker_worker.emit = lambda event: events.append(event)
        speaker_worker.pcm_to_waveform = lambda pcm: len(pcm)
        try:
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.diarization_pipeline = FakePipeline()
            engine.ensure_loaded = lambda: None
            engine._transcribe_wav_parts = lambda _wav_path: [speaker_worker.TimedTextPart("text", 0, 3000, False)]

            engine.transcribe("system", tone_pcm(4), 0)
            engine.transcribe("system", tone_pcm(3), 4000)
        finally:
            speaker_worker.emit = original_emit
            speaker_worker.pcm_to_waveform = original_waveform

        captions = [event for event in events if event.get("type") == "final_caption"]
        self.assertEqual("speaker_2", captions[-1]["speakerId"])

    def test_speaker_embedding_keeps_different_window_local_labels_separate(self) -> None:
        class FakeDiarization:
            def __init__(self, embedding):
                self.speaker_embeddings = [embedding]
                self.speaker_diarization = self
                self.exclusive_speaker_diarization = self

            def labels(self):
                return ["SPEAKER_00"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 1), None, "SPEAKER_00"

        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        engine = speaker_worker.LocalSpeechEngine(Path("models"))

        first = engine._stable_speaker_key(FakeDiarization([1.0, 0.0]), "SPEAKER_00")
        second = engine._stable_speaker_key(FakeDiarization([0.0, 1.0]), "SPEAKER_00")
        third = engine._stable_speaker_key(FakeDiarization([0.95, 0.05]), "SPEAKER_00")

        self.assertNotEqual(first, second)
        self.assertEqual(first, third)

    def test_select_current_speaker_label_unwraps_pyannote_diarize_output(self) -> None:
        class Segment:
            def __init__(self, start, end):
                self.start = start
                self.end = end

        class FakeAnnotation:
            def labels(self):
                return ["SPEAKER_00"]

            def itertracks(self, yield_label=False):
                if yield_label:
                    yield Segment(0, 1), None, "SPEAKER_00"

        class FakeDiarizeOutput:
            def __init__(self):
                self.exclusive_speaker_diarization = FakeAnnotation()
                self.speaker_diarization = FakeAnnotation()

        label = speaker_worker.select_current_speaker_label(
            FakeDiarizeOutput(),
            context_seconds=1,
            current_seconds=1,
        )

        self.assertEqual("SPEAKER_00", label)

    def test_parse_worker_command_accepts_utf8_bom(self) -> None:
        parsed = speaker_worker.parse_worker_command('\ufeff{"type":"start"}')

        self.assertEqual({"type": "start"}, parsed)

    def test_materialize_hf_cache_links_replaces_snapshot_symlink_with_file(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            blob = root / "blobs" / "abc"
            link = root / "snapshots" / "rev" / "config.yaml"
            blob.parent.mkdir(parents=True)
            link.parent.mkdir(parents=True)
            blob.write_text("config", encoding="utf-8")
            try:
                os.symlink(Path("..") / ".." / "blobs" / "abc", link)
            except OSError as exc:
                self.skipTest(f"symlink creation is not available: {exc}")

            materialized = speaker_worker.materialize_hf_cache_links(root)

            self.assertTrue(materialized)
            self.assertFalse(link.is_symlink())
            self.assertEqual("config", link.read_text(encoding="utf-8"))

    def test_speaker_registry_reuses_matching_active_slot(self) -> None:
        from diarization_state import SpeakerCountPolicy, SpeakerObservation, SpeakerSlotRegistry

        registry = SpeakerSlotRegistry(SpeakerCountPolicy(mode="active_max", max_speakers=3))
        first = registry.assign(SpeakerObservation(label="A", start_ms=0, end_ms=2000, embedding=[1.0, 0.0]))
        second = registry.assign(SpeakerObservation(label="A2", start_ms=3000, end_ms=5000, embedding=[0.98, 0.02]))

        self.assertEqual("speaker_1", first.speaker_id)
        self.assertEqual("speaker_1", second.speaker_id)

    def test_speaker_registry_replaces_inactive_slot_in_active_max_mode(self) -> None:
        from diarization_state import SpeakerCountPolicy, SpeakerObservation, SpeakerSlotRegistry

        registry = SpeakerSlotRegistry(SpeakerCountPolicy(mode="active_max", max_speakers=2, inactive_after_ms=10_000))
        registry.assign(SpeakerObservation(label="A", start_ms=0, end_ms=2000, embedding=[1.0, 0.0]))
        registry.assign(SpeakerObservation(label="B", start_ms=1000, end_ms=3000, embedding=[0.0, 1.0]))
        assigned = registry.assign(SpeakerObservation(label="C", start_ms=20_000, end_ms=22_000, embedding=[-1.0, 0.0]))

        self.assertEqual("speaker_1", assigned.speaker_id)
        self.assertEqual("replaced_inactive", assigned.reason)

    def test_speaker_registry_does_not_replace_in_exact_mode(self) -> None:
        from diarization_state import SpeakerCountPolicy, SpeakerObservation, SpeakerSlotRegistry, UNKNOWN_SPEAKER_ID

        registry = SpeakerSlotRegistry(SpeakerCountPolicy(mode="exact", max_speakers=2, exact_speakers=2, inactive_after_ms=10_000))
        registry.assign(SpeakerObservation(label="A", start_ms=0, end_ms=2000, embedding=[1.0, 0.0]))
        registry.assign(SpeakerObservation(label="B", start_ms=1000, end_ms=3000, embedding=[0.0, 1.0]))
        assigned = registry.assign(SpeakerObservation(label="C", start_ms=20_000, end_ms=22_000, embedding=[-1.0, 0.0]))

        self.assertEqual(UNKNOWN_SPEAKER_ID, assigned.speaker_id)
        self.assertEqual("exact_pool_full", assigned.reason)

    def test_worker_active_max_replaces_inactive_speaker_slot(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "legacy"
        engine.config.max_speakers = 2
        engine.config.exact_speakers = None
        engine.config.speaker_count_mode = "active_max"
        engine.reset_speaker_state()

        first = engine._speaker_id_for_embedding("system", "A", [1.0, 0.0], 0, 2000)
        second = engine._speaker_id_for_embedding("system", "B", [0.0, 1.0], 1000, 3000)
        pending = engine._speaker_id_for_embedding("system", "C", [-1.0, 0.0], 20_000, 22_000)
        replacement = engine._speaker_id_for_embedding("system", "C", [-0.99, 0.01], 22_500, 24_500)

        self.assertEqual("speaker_1", first)
        self.assertEqual("speaker_2", second)
        self.assertEqual("speaker_unknown", pending)
        self.assertEqual("speaker_1", replacement)

    def test_diart_uses_streaming_speaker_policy(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "diart"
        engine.config.max_speakers = 4
        engine.config.exact_speakers = None
        engine.config.speaker_count_mode = "active_max"

        policy = engine._speaker_count_policy()

        self.assertEqual("active_max", policy.mode)
        self.assertEqual(60_000, policy.inactive_after_ms)
        self.assertEqual(15_000, policy.protected_after_ms)
        self.assertEqual(2, policy.pending_confirmations)
        self.assertEqual(0.12, policy.embedding_update_rate)

    def test_sortformer_external_labels_are_smoothed_by_registry(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "sortformer"
        engine.config.max_speakers = 4
        engine.config.exact_speakers = None
        engine.config.speaker_count_mode = "active_max"
        engine.reset_speaker_state()

        first = engine._speaker_id_for_external_label("system", "SORTFORMER_0", 0, 2000)
        second = engine._speaker_id_for_external_label("system", "SORTFORMER_0", 2500, 4500)

        self.assertEqual("speaker_1", first)
        self.assertEqual("speaker_1", second)

    def test_sortformer_uses_external_label_smoothing_policy(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "sortformer"
        engine.config.max_speakers = 4
        engine.config.exact_speakers = None
        engine.config.speaker_count_mode = "active_max"

        policy = engine._speaker_count_policy()

        self.assertEqual("active_max", policy.mode)
        self.assertEqual(45_000, policy.inactive_after_ms)
        self.assertEqual(10_000, policy.protected_after_ms)
        self.assertEqual(3, policy.pending_confirmations)

    def test_pyannote_active_max_uses_conservative_speaker_policy(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "pyannote_community"
        engine.config.max_speakers = 4
        engine.config.exact_speakers = None
        engine.config.speaker_count_mode = "active_max"

        policy = engine._speaker_count_policy()

        self.assertEqual("active_max", policy.mode)
        self.assertEqual(120_000, policy.inactive_after_ms)
        self.assertEqual(1, policy.pending_confirmations)
        self.assertEqual(0.10, policy.embedding_update_rate)

    def test_pyannote_exact_mode_marks_out_of_pool_voice_unknown(self) -> None:
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.diarization_model = "pyannote_community"
        engine.config.exact_speakers = 2
        engine.config.max_speakers = 2
        engine.reset_speaker_state()

        engine._speaker_id_for_embedding("system", "A", [1.0, 0.0], 0, 2000)
        engine._speaker_id_for_embedding("system", "B", [0.0, 1.0], 1000, 3000)
        assigned = engine._speaker_id_for_embedding("system", "C", [-1.0, 0.0], 4000, 6000)

        self.assertEqual("speaker_unknown", assigned)

    def test_speech_separation_model_normalization_only_accepts_integrated_models(self) -> None:
        self.assertEqual("mossformer2_ss_16k", speaker_worker.normalize_speech_separation_model("MossFormer2_SS_16K"))
        self.assertEqual("sepformer_whamr16k", speaker_worker.normalize_speech_separation_model("SepFormer"))
        self.assertEqual("none", speaker_worker.normalize_speech_separation_model("RE-SepFormer"))
        self.assertEqual("none", speaker_worker.normalize_speech_separation_model("TF-GridNet"))

    def test_speech_separation_uses_two_second_chunks_and_replaces_streaming_diarization(self) -> None:
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            engine = speaker_worker.LocalSpeechEngine(Path("models"))
            engine.configure({
                "type": "configure",
                "sttQualityPreset": 100,
                "diarizationEnabled": True,
                "diarizationModel": "diart",
                "speechSeparationModel": "mossformer2_ss_16k",
            })

        self.assertEqual("mossformer2_ss_16k", engine.config.speech_separation_model)
        self.assertEqual(int(speaker_worker.BYTES_PER_SECOND * 1.75), engine.transcribe_chunk_bytes())
        self.assertFalse(engine._uses_streaming_diarization())

    def test_separated_channel_alignment_keeps_speaker_order_after_model_swap(self) -> None:
        import numpy as np

        first = np.linspace(-0.2, 0.2, 1000, dtype=np.float32)
        second = np.sin(np.linspace(0, 12, 1000, dtype=np.float32)) * 0.1
        stems = [
            np.concatenate([second[-200:], np.full(50, 0.2, dtype=np.float32)]),
            np.concatenate([first[-200:], np.full(50, 0.1, dtype=np.float32)]),
        ]

        aligned = speaker_worker.align_separated_channels(stems, [first[-200:], second[-200:]], 200)

        self.assertEqual(2, len(aligned))
        self.assertAlmostEqual(0.1, float(np.mean(aligned[0])), places=5)
        self.assertAlmostEqual(0.2, float(np.mean(aligned[1])), places=5)

    def test_overlap_separation_emits_independent_captions_for_two_stems(self) -> None:
        import numpy as np

        samples = np.arange(speaker_worker.SAMPLE_RATE * 2, dtype=np.float32)
        first = np.sin(2 * np.pi * 220 * samples / speaker_worker.SAMPLE_RATE).astype(np.float32) * 0.12
        second = np.sin(2 * np.pi * 440 * samples / speaker_worker.SAMPLE_RATE).astype(np.float32) * 0.08
        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.speech_separation_model = "mossformer2_ss_16k"
        engine.config.diarization_enabled = True
        engine.speech_separator = types.SimpleNamespace(separate=lambda _pcm: [first, second])
        captions = iter([
            [speaker_worker.TimedTextPart("hello", 0, 1500, True)],
            [speaker_worker.TimedTextPart("안녕하세요", 0, 1500, True)],
        ])
        engine._transcribe_wav_parts = lambda _path: next(captions)

        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            handled = engine._try_transcribe_separated_speech(
                "system",
                tone_pcm(2),
                1000,
                time.perf_counter(),
            )

        events = [json.loads(line) for line in output.getvalue().splitlines()]
        final_events = [event for event in events if event["type"] == "final_caption"]
        self.assertTrue(handled)
        self.assertEqual(2, len(final_events))
        self.assertEqual({"speaker_1", "speaker_2"}, {event["speakerId"] for event in final_events})
        self.assertEqual({"hello", "안녕하세요"}, {event["text"] for event in final_events})

    def test_overlap_separation_batches_two_qwen_stems(self) -> None:
        import numpy as np

        samples = np.arange(speaker_worker.SAMPLE_RATE * 2, dtype=np.float32)
        first = np.sin(2 * np.pi * 220 * samples / speaker_worker.SAMPLE_RATE).astype(np.float32) * 0.12
        second = np.sin(2 * np.pi * 440 * samples / speaker_worker.SAMPLE_RATE).astype(np.float32) * 0.08
        transcribe_calls = []

        class QwenResult:
            def __init__(self, text):
                self.text = text
                self.time_stamps = []
                self.language = "Korean"

        class FakeQwen:
            def transcribe(self, audio, **kwargs):
                transcribe_calls.append((audio, kwargs))
                return [QwenResult("첫 번째"), QwenResult("두 번째")]

        engine = speaker_worker.LocalSpeechEngine(Path("models"))
        engine.config.asr_engine = "qwen3_asr_diarization"
        engine.config.speech_separation_model = "mossformer2_ss_16k"
        engine.config.diarization_enabled = False
        engine.speech_separator = types.SimpleNamespace(separate=lambda _pcm: [first, second])
        engine.qwen_model = FakeQwen()

        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            handled = engine._try_transcribe_separated_speech(
                "system",
                tone_pcm(2),
                1000,
                time.perf_counter(),
            )

        events = [json.loads(line) for line in output.getvalue().splitlines()]
        final_events = [event for event in events if event["type"] == "final_caption"]
        self.assertTrue(handled)
        self.assertEqual(1, len(transcribe_calls))
        self.assertEqual(2, len(transcribe_calls[0][0]))
        self.assertFalse(transcribe_calls[0][1]["return_time_stamps"])
        self.assertEqual({"첫 번째", "두 번째"}, {event["text"] for event in final_events})

    def test_mossformer_adapter_batches_input_and_trims_model_padding(self) -> None:
        import numpy as np

        observed_shapes = []
        observed_devices = []

        class FakeTorchModel:
            def to(self, device):
                observed_devices.append(str(device))
                return self

            def eval(self):
                return self

        class FakeClearVoice:
            def __init__(self, task, model_names):
                self.task = task
                self.model_names = model_names
                self.models = [types.SimpleNamespace(
                    model=FakeTorchModel(),
                    device=None,
                    args=types.SimpleNamespace(use_cuda=0),
                )]

            def __call__(self, samples):
                observed_shapes.append(samples.shape)
                padded = samples.shape[1] + 100
                return np.stack([
                    np.ones(padded, dtype=np.float32),
                    np.full(padded, 0.5, dtype=np.float32),
                ])[:, None, :]

        original = sys.modules.get("clearvoice")
        sys.modules["clearvoice"] = types.SimpleNamespace(ClearVoice=FakeClearVoice)
        try:
            with tempfile.TemporaryDirectory() as temp_dir:
                separator = speaker_worker.MossFormer2Separator(Path(temp_dir), "cuda")
                stems = separator.separate(tone_pcm(1))
        finally:
            if original is None:
                sys.modules.pop("clearvoice", None)
            else:
                sys.modules["clearvoice"] = original

        self.assertEqual([(1, speaker_worker.SAMPLE_RATE)], observed_shapes)
        self.assertEqual(["cuda"], observed_devices)
        self.assertEqual("cuda", str(separator.model.models[0].device))
        self.assertEqual(1, separator.model.models[0].args.use_cuda)
        self.assertEqual([speaker_worker.SAMPLE_RATE, speaker_worker.SAMPLE_RATE], [len(stem) for stem in stems])

    def test_speech_separation_preparation_checks_model_specific_files(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            moss = root / "speech-separation" / "mossformer2" / "checkpoints" / "MossFormer2_SS_16K"
            moss.mkdir(parents=True)
            (moss / "last_best_checkpoint").write_text("checkpoint.pt", encoding="utf-8")
            sep = root / "speech-separation" / "sepformer-whamr16k"
            sep.mkdir(parents=True)
            (sep / "hyperparams.yaml").write_text("modules: {}", encoding="utf-8")
            (sep / "model.ckpt").write_bytes(b"checkpoint")

            self.assertTrue(speaker_worker.is_speech_separation_model_prepared(root, "mossformer2_ss_16k"))
            self.assertTrue(speaker_worker.is_speech_separation_model_prepared(root, "sepformer_whamr16k"))
            self.assertTrue(speaker_worker.is_speech_separation_model_prepared(root, "none"))

    def test_qwen_snapshot_preparation_requires_complete_valid_files(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            models_dir = Path(temp_dir)
            snapshot = (
                models_dir
                / "huggingface"
                / "hub"
                / "models--Qwen--Qwen3-ASR-0.6B"
                / "snapshots"
                / "revision"
            )
            snapshot.mkdir(parents=True)
            (snapshot / "config.json").write_text('{"model_type":"qwen3_asr"}', encoding="utf-8")

            self.assertFalse(speaker_worker.qwen_cached_snapshot_prepared(models_dir, "Qwen/Qwen3-ASR-0.6B"))

            for name in speaker_worker.QWEN_SNAPSHOT_REQUIRED_FILES:
                if name == "config.json":
                    continue
                (snapshot / name).write_bytes(b"ready")
            (snapshot / "model.safetensors").write_bytes(b"weights")

            self.assertTrue(speaker_worker.qwen_cached_snapshot_prepared(models_dir, "Qwen/Qwen3-ASR-0.6B"))
            (snapshot / "model.safetensors").unlink()
            (snapshot / "model.safetensors.index.json").write_text(
                '{"weight_map":{"first":"model-00001-of-00002.safetensors","second":"model-00002-of-00002.safetensors"}}',
                encoding="utf-8",
            )
            (snapshot / "model-00001-of-00002.safetensors").write_bytes(b"weights")
            self.assertFalse(speaker_worker.qwen_cached_snapshot_prepared(models_dir, "Qwen/Qwen3-ASR-0.6B"))
            (snapshot / "model-00002-of-00002.safetensors").write_bytes(b"weights")
            self.assertTrue(speaker_worker.qwen_cached_snapshot_prepared(models_dir, "Qwen/Qwen3-ASR-0.6B"))
            (snapshot / "config.json").write_text('{"model_type":"wrong"}', encoding="utf-8")
            self.assertFalse(speaker_worker.qwen_cached_snapshot_prepared(models_dir, "Qwen/Qwen3-ASR-0.6B"))


if __name__ == "__main__":
    unittest.main()
