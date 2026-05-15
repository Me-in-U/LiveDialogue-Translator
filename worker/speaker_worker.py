from __future__ import annotations

import argparse
import asyncio
import base64
import contextlib
import io
import json
import math
import os
import queue
import shutil
import sys
import tempfile
import threading
import time
import unicodedata
import warnings
import wave
import importlib.util
from array import array
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


def canonical_transcription_text(text: str) -> str:
    normalized = unicodedata.normalize("NFKC", text).casefold()
    return "".join(
        character
        for character in normalized
        if not character.isspace() and unicodedata.category(character)[0] not in {"P", "S"}
    )


def canonical_transcription_texts(phrases: tuple[str, ...]) -> tuple[str, ...]:
    return tuple(canonical_transcription_text(phrase) for phrase in phrases)


def canonical_transcription_text_set(phrases: tuple[str, ...]) -> set[str]:
    return set(canonical_transcription_texts(phrases))


SAMPLE_RATE = 16000
BYTES_PER_SECOND = SAMPLE_RATE * 2
TRANSCRIBE_CHUNK_SECONDS = 1
TRANSCRIBE_CHUNK_BYTES = BYTES_PER_SECOND * TRANSCRIBE_CHUNK_SECONDS
DIARIZATION_CONTEXT_SECONDS = 30
DIARIZATION_MIN_CONTEXT_SECONDS = 4
QWEN_DIARIZED_CHUNK_SECONDS = DIARIZATION_MIN_CONTEXT_SECONDS
ASR_THREAD_NAME = "live-dialogue-translator-asr"
DIARIZATION_MIN_CONTEXT_BYTES = BYTES_PER_SECOND * DIARIZATION_MIN_CONTEXT_SECONDS
DIARIZATION_EXACT_SPEAKER_CONTEXT_SECONDS = 8
DIART_STREAM_CHUNK_SECONDS = 0.5
DIART_PENDING_MAX_SECONDS = 16
DIART_MIN_CAPTION_COVERAGE_RATIO = 0.2
DIARIZATION_MODEL_ID = "pyannote/speaker-diarization-community-1"
DIART_SEGMENTATION_MODEL_ID = "pyannote/segmentation"
DIART_EMBEDDING_MODEL_ID = "pyannote/embedding"
QWEN_FORCED_ALIGNER_LANGUAGES = {
    "cantonese",
    "chinese",
    "english",
    "french",
    "german",
    "italian",
    "japanese",
    "korean",
    "portuguese",
    "russian",
    "spanish",
}
QWEN_TRANSCRIPTION_CONTEXT = (
    "Transcribe the audio verbatim in the requested language. "
    "Include natural punctuation and sentence boundaries. "
    "Return only the transcription text."
)
QWEN_SENTENCE_PAUSE_MS = 700
QWEN_TERMINAL_PUNCTUATION = ".!?。！？"
QWEN_TRAILING_CLOSERS = "\"')]}>”’」』》）"
QWEN_CJK_SENTENCE_LANGUAGES = {"cantonese", "chinese", "japanese"}
VOICE_RMS_THRESHOLD = 120.0
STABLE_SPEAKER_WINDOWS = 2
DIARIZATION_DECISION_SECONDS = 2.0
DIARIZATION_MIN_LABEL_SECONDS = 0.75
DIARIZATION_MIN_LABEL_RATIO = 0.58
DIARIZATION_HANDOFF_PREROLL_SECONDS = 0.35
DIARIZATION_HANDOFF_MAX_GAP_SECONDS = 0.4
DIARIZATION_MIN_TURN_SLICE_SECONDS = 0.5
DIARIZATION_EMBEDDING_MATCH_THRESHOLD = 0.62
DIARIZATION_EMBEDDING_UPDATE_RATE = 0.15
WHISPER_NO_SPEECH_THRESHOLD = 0.6
WHISPER_LOG_PROB_THRESHOLD = -1.0
WHISPER_COMPRESSION_RATIO_THRESHOLD = 2.4
WHISPER_VAD_MIN_SILENCE_MS = 500
WHISPER_MAX_NEW_TOKENS = 64
WHISPERLIVEKIT_MAX_CAPTION_CHARS = 80
WHISPERLIVEKIT_MAX_CAPTION_MS = 8000
WHISPERLIVEKIT_MAX_CONTEXT_TOKENS = 0
WHISPERLIVEKIT_REVISION_DUPLICATE_MIN_CHARS = 6
WHISPERLIVEKIT_REVISION_DUPLICATE_OVERLAP_RATIO = 0.45
WHISPERLIVEKIT_REVISION_DUPLICATE_TIME_TOLERANCE_MS = 2500
WHISPERLIVEKIT_EMITTED_REF_LIMIT = 400
WHISPERLIVEKIT_MAX_RETROSPECTIVE_MS = 8000
STT_TRIM_FRAME_MS = 30
STT_TRIM_PADDING_MS = 200
STT_MIN_RETAIN_MS = 250
STT_TARGET_RMS = 3000.0
STT_MAX_GAIN = 4.0
STT_PEAK_CEILING = 30000.0
OUTRO_THANKS_HALLUCINATION_PHRASE_TEXTS = (
    "시청해 주셔서 감사합니다",
    "시청해 주셔서 고맙습니다",
    "thank you for watching",
    "thanks for watching",
    "thank you for watching until the end",
    "thank you for watching please subscribe",
    "thanks for watching please subscribe",
    "ご視聴ありがとうございました",
    "見てくれてありがとう",
    "ご覧いただきありがとうございます",
    "最後までご視聴ありがとうございました",
    "谢谢观看 下集再见",
    "謝謝觀看 下次見",
    "多謝您的觀看",
    "شكرا على المشاهدة",
    "شكرا للمشاهدة",
    "gracias por ver el video",
    "obrigado por assistir",
    "obrigada por assistir",
    "merci d'avoir regardé cette vidéo",
    "merci d'avoir regardé",
    "спасибо за просмотр",
    "terima kasih telah menonton",
    "terima kasih sudah menonton",
    "terima kasih kerana menonton",
    "kiitos kun katsoit",
    "grazie per la visione",
    "hvala što pratite kanal",
)
TRANSCRIPTION_HALLUCINATION_EXACT_TEXT_VALUES = (
    "MBC 뉴스",
    "MBC 뉴스입니다",
    "MBC 뉴스데스크",
    "엠비씨 뉴스",
    "엠비씨 뉴스입니다",
    "문화방송입니다",
    "한글자막 by 박진희",
    "지금까지 뉴스 스토리였습니다",
    "구독 좋아요",
    "구독 좋아요 알림 설정",
    "자막 제공",
    "Subtitles by the Amara.org community",
    "Subtitles created by the Amara.org community",
    "Untertitel der Amara.org Community",
    "Sous titres par la communauté d'Amara.org",
    "字幕由 Amara.org 社群提供",
    "字幕由 Amara.org 社区提供",
)
TRANSCRIPTION_HALLUCINATION_MARKER_TEXTS = (
    "MBC 뉴스",
    "MBC News",
    "엠비씨 뉴스",
    "뉴스데스크",
    "뉴스스토리",
    "문화방송",
    "한글자막 by",
    "구독 좋아요",
    "알림 설정",
    "다음 영상",
    "자막 제공",
    "Amara.org",
    "please subscribe",
    "subscribe to my channel",
    "チャンネル登録",
    "登録お願いします",
    "подписывайтесь",
    "abonnez vous",
    "suscríbete al canal",
    "inscreva-se",
    "iscriviti",
    "订阅",
    "訂閱",
)
# Keep hallucination phrases human-readable above; canonicalization absorbs
# spacing, punctuation, casing, and Unicode width variations seen in ASR output.
OUTRO_THANKS_HALLUCINATION_PHRASES = canonical_transcription_texts(OUTRO_THANKS_HALLUCINATION_PHRASE_TEXTS)
TRANSCRIPTION_HALLUCINATION_EXACT_TEXTS = canonical_transcription_text_set(TRANSCRIPTION_HALLUCINATION_EXACT_TEXT_VALUES)
TRANSCRIPTION_HALLUCINATION_MARKERS = canonical_transcription_texts(TRANSCRIPTION_HALLUCINATION_MARKER_TEXTS)


_ASR_ENGINE_DLL_DIRECTORY_HANDLES: list[Any] = []
_ASR_ENGINE_DLL_DIRECTORIES: set[str] = set()
_ASR_ENGINE_BASE_PRELOAD_MODULES = ("torch", "torchgen", "torchaudio")
_ASR_ENGINE_OPTIONAL_PACKAGE_QUARANTINE = ("torchvision",)


def apply_asr_engine_site_path() -> None:
    site = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE", "").strip()
    if not site:
        return

    preload_base_runtime_modules()

    site_paths = [Path(entry) for entry in site.split(os.pathsep) if entry.strip()]
    quarantine_asr_engine_optional_packages(site_paths)
    for site_path in reversed(site_paths):
        site_text = str(site_path)
        sys.path = [entry for entry in sys.path if entry != site_text]
        sys.path.insert(0, site_text)

    add_dll_directory = getattr(os, "add_dll_directory", None)
    if not callable(add_dll_directory):
        return

    candidates = [site_path for site_path in site_paths if site_path.exists()]
    for site_path in site_paths:
        if site_path.exists():
            candidates.extend(path for path in site_path.glob("*.libs") if path.is_dir())
    for candidate in candidates:
        candidate_text = str(candidate)
        if candidate_text in _ASR_ENGINE_DLL_DIRECTORIES:
            continue
        try:
            handle = add_dll_directory(candidate_text)
        except OSError:
            continue
        _ASR_ENGINE_DLL_DIRECTORIES.add(candidate_text)
        _ASR_ENGINE_DLL_DIRECTORY_HANDLES.append(handle)


def preload_base_runtime_modules() -> None:
    for name in _ASR_ENGINE_BASE_PRELOAD_MODULES:
        if name in sys.modules:
            continue
        try:
            __import__(name)
        except Exception:
            continue


def quarantine_asr_engine_optional_packages(site_paths: list[Path]) -> None:
    for site_path in site_paths:
        if not site_path.exists():
            continue
        for package_name in _ASR_ENGINE_OPTIONAL_PACKAGE_QUARANTINE:
            quarantine_asr_engine_package(site_path, package_name)


def quarantine_asr_engine_package(site_path: Path, package_name: str) -> None:
    candidates = [site_path / package_name]
    candidates.extend(site_path.glob(f"{package_name}-*.dist-info"))
    for candidate in candidates:
        if not candidate.exists():
            continue
        disabled = candidate.with_name(f"{candidate.name}.live_dialogue_translator_disabled")
        try:
            if disabled.exists():
                if candidate.is_dir():
                    shutil.rmtree(candidate)
                else:
                    candidate.unlink()
            else:
                candidate.rename(disabled)
        except OSError:
            continue


apply_asr_engine_site_path()


def emit(event: dict[str, Any]) -> None:
    print(json.dumps(event, ensure_ascii=False), flush=True)


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def suppress_torchcodec_warning() -> None:
    warnings.filterwarnings(
        "ignore",
        message=r"\s*torchcodec is not installed correctly.*",
        category=UserWarning,
    )


def apply_torchaudio_compatibility_shims() -> None:
    try:
        import torchaudio
    except Exception:
        return

    if not hasattr(torchaudio, "set_audio_backend"):
        setattr(torchaudio, "set_audio_backend", lambda *_args, **_kwargs: None)


def patch_speechbrain_lazy_module_inspection() -> None:
    try:
        import importlib
        importutils = sys.modules.get("speechbrain.utils.importutils")
        if importutils is None:
            import speechbrain.utils.importutils as importutils
    except Exception:
        return

    lazy_module_class = getattr(importutils, "LazyModule", None)
    if lazy_module_class is None or getattr(lazy_module_class, "_live_dialogue_translator_patched", False):
        return

    def ensure_module(self, stacklevel: int):
        for depth in range(1, stacklevel + 6):
            try:
                filename = sys._getframe(depth).f_code.co_filename.replace("\\", "/")
            except ValueError:
                break
            if filename == "inspect.py" or filename.endswith("/inspect.py"):
                raise AttributeError()

        if getattr(self, "lazy_module", None) is None:
            package = getattr(self, "package", None)
            target = getattr(self, "target")
            if package is None:
                self.lazy_module = importlib.import_module(target)
            else:
                self.lazy_module = importlib.import_module(f".{target}", package)
        return self.lazy_module

    lazy_module_class.ensure_module = ensure_module
    lazy_module_class._live_dialogue_translator_patched = True


def call_without_stdout_noise(callback):
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return callback()


def from_pretrained_with_token(loader, model_id: str, token: str, **kwargs):
    patch_speechbrain_lazy_module_inspection()

    def invoke():
        try:
            return loader(model_id, token=token, **kwargs)
        except TypeError as exc:
            if "token" not in str(exc):
                raise
            return loader(model_id, use_auth_token=token, **kwargs)

    return call_without_stdout_noise(invoke)


def is_hf_access_error(error: Any) -> bool:
    text = str(error).lower()
    return (
        "gated repo" in text
        or "authorized list" in text
        or "user agreement" in text
        or "403 client error" in text
        or "repository is private or gated" in text
        or "access to model pyannote/" in text
    )


def hf_access_denied_message(error: Any, diarization_model: str) -> str:
    if normalize_diarization_model(diarization_model) == "diart":
        requirement = (
            "Diart requires Hugging Face access to pyannote/segmentation and pyannote/embedding. "
            "Open Model Manager, accept both model terms, then save a token with public gated repo read access."
        )
    else:
        requirement = (
            "Community-1 requires Hugging Face access to pyannote/speaker-diarization-community-1. "
            "Open Model Manager, accept the model terms, then save a token with public gated repo read access."
        )
    return f"{requirement}\n{error}"


def parse_optional_positive_int(value: Any) -> int | None:
    if value is None or value == "":
        return None
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return None
    return parsed if parsed > 0 else None


def clamp_quality_preset(value: Any) -> int:
    try:
        parsed = int(round(float(value)))
    except (TypeError, ValueError):
        return 50
    return max(0, min(100, parsed))


def clamp_float(value: Any, default: float, minimum: float, maximum: float) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        parsed = default
    return max(minimum, min(maximum, parsed))


def normalize_language_codes(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []

    normalized: list[str] = []
    for item in value:
        language = str(item).strip().lower()
        if not language or language == "auto" or language in normalized:
            continue
        normalized.append(language)
    return normalized


def normalize_diarization_model(value: Any) -> str:
    model = str(value or "pyannote_community").strip().lower().replace("-", "_")
    if model in {"diart", "diart_realtime"}:
        return "diart"
    if model in {"sortformer", "nvidia_sortformer"}:
        return "sortformer"
    return "pyannote_community"


def normalize_asr_engine(value: Any) -> str:
    engine = str(value or os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE", "faster_whisper")).strip().lower().replace("-", "_")
    if engine in {"none", "default", "faster_whisper", "fasterwhisper", "whisper"}:
        return "faster_whisper"
    if engine in {"qwen3_asr", "qwen3_asr_diarization", "qwen"}:
        return "qwen3_asr_diarization"
    if engine in {"whisperlivekit", "whisperlivekit_sortformer", "wlk_sortformer"}:
        return "whisperlivekit_sortformer"
    if engine in {"whisperx", "whisper_x"}:
        return "whisperx"
    return "faster_whisper"


def configure_huggingface_cache(models_dir: Path) -> None:
    cache_home = models_dir / "huggingface"
    hub_cache = cache_home / "hub"
    os.environ["HF_HOME"] = str(cache_home)
    os.environ["HUGGINGFACE_HUB_CACHE"] = str(hub_cache)
    os.environ["HF_HUB_CACHE"] = str(hub_cache)


@dataclass
class WorkerConfig:
    input_mode: str = "system_and_mic"
    stt_model: str = "small"
    stt_languages: list[str] = field(default_factory=list)
    stt_quality_preset: int = 0
    diarization_quality_preset: int = 100
    compute_mode: str = "auto"
    asr_engine: str = "faster_whisper"
    diarization_enabled: bool = True
    diarization_model: str = "pyannote_community"
    max_speakers: int = 6
    exact_speakers: int | None = None
    show_latency: bool = True
    speaker_names: dict[str, str] = field(default_factory=dict)
    diart_manual_settings: bool = False
    diart_duration_seconds: float = 5.0
    diart_step_seconds: float = 0.5
    diart_latency_seconds: float = 1.0
    diart_tau_active: float = 0.555
    diart_rho_update: float = 0.422
    diart_delta_new: float = 1.517


@dataclass(frozen=True)
class SttPreprocessResult:
    pcm: bytes
    leading_trim_ms: int
    trailing_trim_ms: int


@dataclass(frozen=True)
class TimedTextPart:
    text: str
    start_ms: int
    end_ms: int
    has_word_timing: bool


class WorkerStopRequested(RuntimeError):
    pass


class LocalSpeechEngine:
    def __init__(self, models_dir: Path) -> None:
        self.models_dir = models_dir
        self.config = WorkerConfig()
        self.whisper_model = None
        self.qwen_model = None
        self.qwen_fallback_whisper_model = None
        self.whisperlivekit_engine = None
        self.whisperlivekit_session = None
        self.whisperlivekit_session_lock = threading.RLock()
        self.whisperx_engine = None
        self.diarization_pipeline = None
        self.warned_mock = False
        self.speaker_ids: dict[str, str] = {}
        self.speaker_embeddings: dict[str, Any] = {}
        self.diarization_buffers: dict[str, bytearray] = {}
        self.pending_segment_speakers: dict[str, str] = {}
        self.pending_segment_counts: dict[str, int] = {}
        self.stable_segment_speakers: dict[str, str] = {}
        self.last_emitted_segment_speakers: dict[str, str] = {}
        self.last_diarization_speakers: dict[str, str] = {}
        self.streaming_diarization_segments: dict[str, list[tuple[int, int, str]]] = {}
        self.streaming_diarization_start_ms: dict[str, int] = {}
        self.last_stt_error: str | None = None
        self.last_diarization_error: str | None = None
        self.qwen_alignment_debug_emitted = False
        self.qwen_empty_result_debug_emitted = False
        self.qwen_fallback_debug_emitted = False
        self.qwen_fallback_retry_debug_emitted = False
        self.qwen_timeout_debug_emitted = False
        self.qwen_slow_debug_emitted = False
        self.qwen_running_debug_emitted = False
        self.qwen_disabled_reason: str | None = None
        self.last_qwen_call_seconds = 0.0
        self.streaming_diarization_busy = False
        self.streaming_diarization_generation = 0
        self.streaming_diarization_lock = threading.RLock()
        self.streaming_diarization_thread: threading.Thread | None = None
        self.streaming_diarization_pending: dict[str, list[Any]] = {}

    def configure(self, payload: dict[str, Any]) -> None:
        previous = self.config
        exact_speakers = parse_optional_positive_int(payload.get("exactSpeakers"))
        max_speakers = int(payload.get("maxSpeakers", exact_speakers or 6))
        next_config = WorkerConfig(
            input_mode=payload.get("inputMode", "system_and_mic"),
            stt_model=payload.get("sttModel", "small"),
            stt_languages=normalize_language_codes(payload.get("sttLanguages", [])),
            stt_quality_preset=clamp_quality_preset(payload.get("sttQualityPreset", 50)),
            diarization_quality_preset=clamp_quality_preset(payload.get("diarizationQualityPreset", payload.get("sttQualityPreset", 50))),
            compute_mode=payload.get("computeMode", "auto"),
            asr_engine=normalize_asr_engine(payload.get("asrEngine")),
            diarization_enabled=bool(payload.get("diarizationEnabled", True)),
            diarization_model=normalize_diarization_model(payload.get("diarizationModel")),
            max_speakers=max(1, max_speakers),
            exact_speakers=exact_speakers,
            show_latency=bool(payload.get("showLatency", True)),
            speaker_names=dict(payload.get("speakerNames", {})),
            diart_manual_settings=bool(payload.get("diartManualSettings", False)),
            diart_duration_seconds=clamp_float(payload.get("diartDurationSeconds"), 5.0, 3.0, 12.0),
            diart_step_seconds=clamp_float(payload.get("diartStepSeconds"), 0.5, 0.25, 1.0),
            diart_latency_seconds=clamp_float(payload.get("diartLatencySeconds"), 1.0, 0.5, 5.0),
            diart_tau_active=clamp_float(payload.get("diartTauActive"), 0.555, 0.3, 0.9),
            diart_rho_update=clamp_float(payload.get("diartRhoUpdate"), 0.422, 0.0, 1.0),
            diart_delta_new=clamp_float(payload.get("diartDeltaNew"), 1.517, 0.3, 2.0),
        )
        stt_changed = (
            previous.stt_model != next_config.stt_model
            or previous.stt_languages != next_config.stt_languages
            or previous.stt_quality_preset != next_config.stt_quality_preset
            or previous.compute_mode != next_config.compute_mode
            or previous.asr_engine != next_config.asr_engine
        )
        diarization_changed = (
            previous.compute_mode != next_config.compute_mode
            or previous.diarization_enabled != next_config.diarization_enabled
            or previous.diarization_model != next_config.diarization_model
            or previous.diarization_quality_preset != next_config.diarization_quality_preset
            or previous.max_speakers != next_config.max_speakers
            or previous.exact_speakers != next_config.exact_speakers
            or previous.diart_manual_settings != next_config.diart_manual_settings
            or previous.diart_duration_seconds != next_config.diart_duration_seconds
            or previous.diart_step_seconds != next_config.diart_step_seconds
            or previous.diart_latency_seconds != next_config.diart_latency_seconds
            or previous.diart_tau_active != next_config.diart_tau_active
            or previous.diart_rho_update != next_config.diart_rho_update
            or previous.diart_delta_new != next_config.diart_delta_new
        )
        if stt_changed:
            self.close_whisperlivekit_session()
            self.whisper_model = None
            self.qwen_model = None
            self.qwen_fallback_whisper_model = None
            self.whisperlivekit_engine = None
            self.whisperx_engine = None
            self.warned_mock = False
            self.last_stt_error = None
            self.qwen_alignment_debug_emitted = False
            self.qwen_empty_result_debug_emitted = False
            self.qwen_fallback_debug_emitted = False
            self.qwen_fallback_retry_debug_emitted = False
            self.qwen_timeout_debug_emitted = False
            self.qwen_slow_debug_emitted = False
            self.qwen_running_debug_emitted = False
            self.qwen_disabled_reason = None
            self.last_qwen_call_seconds = 0.0
        if diarization_changed:
            if self._config_uses_whisperlivekit_sortformer(previous) or next_config.asr_engine == "whisperlivekit_sortformer":
                self.close_whisperlivekit_session()
            self.diarization_pipeline = None
            self.last_diarization_error = None
        if stt_changed or diarization_changed:
            self.speaker_ids.clear()
            self.speaker_embeddings.clear()
            self.diarization_buffers.clear()
            self.pending_segment_speakers.clear()
            self.pending_segment_counts.clear()
            self.stable_segment_speakers.clear()
            self.last_emitted_segment_speakers.clear()
            self.last_diarization_speakers.clear()
            self.streaming_diarization_segments.clear()
            self.streaming_diarization_start_ms.clear()
            self.streaming_diarization_pending.clear()
            self.streaming_diarization_generation += 1

        self.config = next_config
        speaker_count = f"speaker_cap={self.config.exact_speakers}" if self.config.exact_speakers else f"max_speakers={self.config.max_speakers}"
        language_message = ",".join(self.config.stt_languages) if self.config.stt_languages else "auto"
        context_message = "stream" if self._uses_streaming_diarization() else f"{self._diarization_context_seconds()}s"
        effective_diarization_enabled = self.config.diarization_enabled
        effective_diarization_model = self.config.diarization_model
        manual_diart_message = (
            f", diart_manual=duration:{self.config.diart_duration_seconds:g}s/step:{self.config.diart_step_seconds:g}s/latency:{self.config.diart_latency_seconds:g}s"
            if self.config.diarization_model == "diart" and self.config.diart_manual_settings
            else ""
        )
        emit({
            "type": "model_status",
            "stage": "configured",
            "message": f"engine={self.config.asr_engine}, model={self.config.stt_model}, languages={language_message}, asr_quality={self.config.stt_quality_preset}, diarization_quality={self.config.diarization_quality_preset}, compute={self.config.compute_mode}, diarization={'on' if effective_diarization_enabled else 'off'}, diarization_model={effective_diarization_model}, context={context_message}, {speaker_count}{manual_diart_message}",
            "progress": None,
        })

    def close_whisperlivekit_session(self) -> None:
        with self.whisperlivekit_session_lock:
            session = self.whisperlivekit_session
            self.whisperlivekit_session = None
        if session is not None:
            close = getattr(session, "close", None)
            if callable(close):
                close()

    def _whisperlivekit_streaming_session(self) -> Any:
        with self.whisperlivekit_session_lock:
            if self.whisperlivekit_session is None:
                self.whisperlivekit_session = WhisperLiveKitStreamingSession(self.whisperlivekit_engine)
            return self.whisperlivekit_session

    @staticmethod
    def _config_uses_whisperlivekit_sortformer(config: WorkerConfig) -> bool:
        return (
            config.asr_engine == "whisperlivekit_sortformer"
            and config.diarization_enabled
            and config.diarization_model == "sortformer"
        )

    def ensure_loaded(self) -> None:
        if self._uses_qwen_asr() and self.qwen_model is None:
            try:
                self.qwen_model = load_qwen_asr_model(self.config.stt_model, self._resolve_stt_device())
                self.whisper_model = True
                self.last_stt_error = None
                aligner_message = " + ForcedAligner" if qwen_forced_aligner_enabled() else ""
                emit({"type": "model_status", "stage": "stt_loaded", "message": f"Qwen3-ASR {qwen_asr_model_id(self.config.stt_model)}{aligner_message}", "progress": 1})
            except Exception as exc:
                self.qwen_model = False
                self.whisper_model = False
                self.last_stt_error = str(exc)
                emit({"type": "error", "code": "stt_unavailable", "message": f"Qwen3-ASR unavailable: {exc}", "recoverable": True})

        elif self._uses_whisperlivekit_asr() and self.whisperlivekit_engine is None:
            try:
                self.whisperlivekit_engine = load_whisperlivekit_engine(
                    self.config.stt_model,
                    self._primary_language(),
                    self._resolve_stt_device(),
                    self.config.exact_speakers or self.config.max_speakers,
                    self.config.diarization_enabled and self.config.diarization_model == "sortformer",
                )
                self.whisper_model = True
                self.last_stt_error = None
                message = "WhisperLiveKit + Sortformer" if self.config.diarization_model == "sortformer" else "WhisperLiveKit"
                emit({"type": "model_status", "stage": "stt_loaded", "message": message, "progress": 1})
            except Exception as exc:
                self.whisperlivekit_engine = False
                self.whisper_model = False
                self.last_stt_error = str(exc)
                emit({"type": "error", "code": "stt_unavailable", "message": f"WhisperLiveKit unavailable: {exc}", "recoverable": True})

        elif self._uses_whisperx_asr() and self.whisperx_engine is None:
            try:
                device = self._resolve_stt_device()
                compute_type = self._resolve_stt_compute_type(device)
                self.whisperx_engine = load_whisperx_model(
                    self.config.stt_model,
                    self._primary_language(),
                    device,
                    compute_type,
                    self.models_dir / "whisperx",
                )
                self.whisper_model = True
                self.last_stt_error = None
                emit({"type": "model_status", "stage": "stt_loaded", "message": f"WhisperX {self.config.stt_model} on {device}", "progress": 1})
            except Exception as exc:
                self.whisperx_engine = False
                self.whisper_model = False
                self.last_stt_error = str(exc)
                emit({"type": "error", "code": "stt_unavailable", "message": f"WhisperX unavailable: {exc}", "recoverable": True})

        elif self.whisper_model is None:
            try:
                from faster_whisper import WhisperModel

                device = self._resolve_stt_device()
                compute_type = self._resolve_stt_compute_type(device)
                self.whisper_model = WhisperModel(
                    self.config.stt_model,
                    device=device,
                    compute_type=compute_type,
                    download_root=str(self.models_dir / "whisper"),
                )
                self.last_stt_error = None
                emit({"type": "model_status", "stage": "stt_loaded", "message": f"faster-whisper {self.config.stt_model} on {device}", "progress": 1})
            except Exception as exc:
                self.whisper_model = False
                self.last_stt_error = str(exc)
                emit({"type": "error", "code": "stt_unavailable", "message": str(exc), "recoverable": True})

        if self.config.diarization_enabled and self.diarization_pipeline is None:
            token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGINGFACE_TOKEN")
            if (
                self.config.diarization_model != "sortformer"
                and not token
                and not is_diarization_model_prepared(self.models_dir, self.config.diarization_model, True)
            ):
                self.diarization_pipeline = False
                emit({"type": "error", "code": "hf_token_missing", "message": "Hugging Face token is required for local diarization models.", "recoverable": True})
                return

            try:
                import torch

                torch_device = self._resolve_diarization_device()
                if self.config.diarization_model == "diart":
                    self.diarization_pipeline = load_diart_pipeline(
                        token,
                        self.models_dir / "diart",
                        torch_device,
                        self.config.diarization_quality_preset,
                        self.config.exact_speakers or self.config.max_speakers,
                        self._diart_duration_seconds(),
                        self._diart_step_seconds(),
                        self._diart_latency_seconds(),
                        self._diart_hyper_parameters(),
                    )
                    model_message = f"Diart realtime pipeline on {torch_device}"
                elif self.config.diarization_model == "sortformer":
                    self.diarization_pipeline = load_sortformer_pipeline(
                        self.config.exact_speakers or self.config.max_speakers,
                    )
                    model_message = "Sortformer streaming pipeline"
                else:
                    self.diarization_pipeline = load_pyannote_pipeline(DIARIZATION_MODEL_ID, token, self.models_dir / "pyannote")
                    if torch_device == "cuda":
                        self.diarization_pipeline.to(torch.device("cuda"))
                    model_message = "pyannote community pipeline"
                self.last_diarization_error = None
                emit({"type": "model_status", "stage": "diarization_loaded", "message": model_message, "progress": 1})
            except Exception as exc:
                self.diarization_pipeline = False
                self.last_diarization_error = str(exc)
                if is_hf_access_error(exc):
                    emit({
                        "type": "error",
                        "code": "hf_access_denied",
                        "message": hf_access_denied_message(exc, self.config.diarization_model),
                        "recoverable": True,
                    })
                else:
                    emit({"type": "error", "code": "diarization_unavailable", "message": str(exc), "recoverable": True})

    def transcribe(self, source: str, pcm: bytes, timestamp_ms: int, queue_diarization: bool = True) -> None:
        if not pcm_has_voice(pcm):
            return

        started = time.perf_counter()
        self.ensure_loaded()
        if self._uses_whisperlivekit_asr():
            self._try_transcribe_whisperlivekit_sortformer(source, pcm, timestamp_ms, started)
            return

        if self._uses_streaming_diarization() and queue_diarization:
            self.queue_streaming_diarization(source, pcm, timestamp_ms)
        else:
            stable_turns_first = self.config.stt_quality_preset >= 75 and not self._uses_qwen_asr()
            if stable_turns_first and self._try_transcribe_diarized_turns(source, pcm, timestamp_ms, started):
                return
            if self._try_transcribe_diarized_words(source, pcm, timestamp_ms, started):
                return
            if not stable_turns_first and self._try_transcribe_diarized_turns(source, pcm, timestamp_ms, started):
                return

        stt_audio = preprocess_stt_pcm(pcm)
        if stt_audio is None:
            return

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            write_wav(wav_path, stt_audio.pcm)
            raw_end_ms = timestamp_ms + int(len(pcm) / BYTES_PER_SECOND * 1000)
            start_ms = timestamp_ms + stt_audio.leading_trim_ms
            end_ms = max(start_ms + 1, raw_end_ms - stt_audio.trailing_trim_ms)
            speaker_id = (
                self._cached_diarization_speaker_for(source, start_ms, end_ms)
                if self._uses_streaming_diarization()
                else self._speaker_for(source, wav_path, pcm)
            ) or self._fallback_speaker_id(source)
            text_parts = self._transcribe_wav_parts(wav_path)
            text = join_text_parts(text_parts)
            if not text:
                return

            latency_ms = int((time.perf_counter() - started) * 1000)
            if self._emit_streaming_diarized_text_parts(
                source,
                text_parts,
                start_ms,
                speaker_id,
                latency_ms,
            ):
                return

            event = {
                "type": "final_caption",
                "speakerId": speaker_id,
                "text": text,
                "startMs": start_ms,
                "endMs": end_ms,
                "latencyMs": latency_ms,
            }
            emit(event)
        finally:
            try:
                wav_path.unlink(missing_ok=True)
            except Exception:
                pass

    def _try_transcribe_diarized_words(self, source: str, pcm: bytes, timestamp_ms: int, started: float) -> bool:
        if (
            self.config.stt_quality_preset < 35 and not self._uses_qwen_asr()
            or self._uses_streaming_diarization()
            or not self.whisper_model
            or not self.diarization_pipeline
            or source == "mic"
        ):
            return False

        current_seconds = len(pcm) / BYTES_PER_SECOND
        if current_seconds < 1.5:
            return False

        stt_audio = preprocess_stt_pcm(pcm)
        if stt_audio is None:
            return False

        try:
            context_pcm = self._diarization_window_for(source, pcm)
            context_seconds = len(context_pcm) / BYTES_PER_SECOND
            if context_seconds < DIARIZATION_MIN_CONTEXT_SECONDS:
                return False

            diarization = self.diarization_pipeline(
                {"waveform": pcm_to_waveform(context_pcm), "sample_rate": SAMPLE_RATE},
                **self._diarization_speaker_kwargs(context_seconds),
            )
            tail_start_seconds = max(0.0, context_seconds - current_seconds)
            turns = speaker_handoff_adjusted_turns(diarization_turns_for_window(
                diarization,
                window_start_seconds=tail_start_seconds,
                window_end_seconds=context_seconds,
            ))
            if not turns:
                return False

            speaker_turns: list[tuple[int, int, str]] = []
            for label, start_seconds, end_seconds in turns:
                relative_start = max(0.0, start_seconds - tail_start_seconds)
                relative_end = min(current_seconds, end_seconds - tail_start_seconds)
                if relative_end <= relative_start:
                    continue

                speaker_id = self._speaker_id_for_label(diarization, label, source)
                start_ms = timestamp_ms + int(relative_start * 1000)
                end_ms = timestamp_ms + int(relative_end * 1000)
                speaker_turns.append((start_ms, end_ms, speaker_id))
                emit({
                    "type": "speaker_segment",
                    "speakerId": speaker_id,
                    "startMs": start_ms,
                    "endMs": end_ms,
                    "confidence": diarization_turn_confidence(relative_start, relative_end, context_seconds),
                })

            if not speaker_turns:
                return False

            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
                wav_path = Path(temp.name)
            try:
                write_wav(wav_path, stt_audio.pcm)
                text_parts = self._transcribe_wav_parts(wav_path)
            finally:
                try:
                    wav_path.unlink(missing_ok=True)
                except Exception:
                    pass

            if not text_parts or not all(part.has_word_timing for part in text_parts):
                return False

            return self._emit_text_parts_for_speaker_turns(
                text_parts,
                speaker_turns,
                timestamp_ms + stt_audio.leading_trim_ms,
                self._fallback_speaker_id(source),
                int((time.perf_counter() - started) * 1000),
            )
        except Exception as exc:
            emit({"type": "error", "code": "diarized_word_stt_failed", "message": str(exc), "recoverable": True})
            return False

    def _try_transcribe_diarized_turns(self, source: str, pcm: bytes, timestamp_ms: int, started: float) -> bool:
        if (
            self.config.stt_quality_preset < 75
            or self._uses_streaming_diarization()
            or not self.whisper_model
            or not self.diarization_pipeline
            or source == "mic"
        ):
            return False

        current_seconds = len(pcm) / BYTES_PER_SECOND
        if current_seconds < DIARIZATION_MIN_CONTEXT_SECONDS:
            return False

        try:
            context_pcm = self._diarization_window_for(source, pcm)
            context_seconds = len(context_pcm) / BYTES_PER_SECOND
            if context_seconds < DIARIZATION_MIN_CONTEXT_SECONDS:
                return False

            diarization = self.diarization_pipeline(
                {"waveform": pcm_to_waveform(context_pcm), "sample_rate": SAMPLE_RATE},
                **self._diarization_speaker_kwargs(context_seconds),
            )
            label = select_current_speaker_label(
                diarization,
                context_seconds=context_seconds,
                current_seconds=current_seconds,
            )
            if label:
                self.last_diarization_speakers[source] = self._speaker_id_for_label(diarization, label, source)
            tail_start_seconds = max(0.0, context_seconds - current_seconds)
            turns = diarization_turns_for_window(
                diarization,
                window_start_seconds=tail_start_seconds,
                window_end_seconds=context_seconds,
            )
            turns = speaker_handoff_adjusted_turns(turns)
            if not turns:
                return False

            emitted = False
            for label, start_seconds, end_seconds in turns:
                relative_start = max(0.0, start_seconds - tail_start_seconds)
                relative_end = min(current_seconds, end_seconds - tail_start_seconds)
                segment_pcm = slice_pcm_seconds(pcm, relative_start, relative_end)
                if not segment_pcm or not pcm_has_voice(segment_pcm):
                    continue

                stt_audio = preprocess_stt_pcm(segment_pcm)
                if stt_audio is None:
                    continue

                speaker_id = self._speaker_id_for_label(diarization, label, source)
                emit({
                    "type": "speaker_segment",
                    "speakerId": speaker_id,
                    "startMs": timestamp_ms + int(relative_start * 1000),
                    "endMs": timestamp_ms + int(relative_end * 1000),
                    "confidence": diarization_turn_confidence(relative_start, relative_end, context_seconds),
                })
                text = self._transcribe_pcm(stt_audio.pcm)
                if not text:
                    continue

                raw_start_ms = timestamp_ms + int(relative_start * 1000)
                raw_end_ms = timestamp_ms + int(relative_end * 1000)
                start_ms = raw_start_ms + stt_audio.leading_trim_ms
                end_ms = max(start_ms + 1, raw_end_ms - stt_audio.trailing_trim_ms)
                emit({
                    "type": "final_caption",
                    "speakerId": speaker_id,
                    "text": text,
                    "startMs": start_ms,
                    "endMs": end_ms,
                    "latencyMs": int((time.perf_counter() - started) * 1000),
                })
                emitted = True

            return emitted
        except Exception as exc:
            emit({"type": "error", "code": "diarized_stt_failed", "message": str(exc), "recoverable": True})
            return False

    def _transcribe_pcm(self, pcm: bytes) -> str:
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
            wav_path = Path(temp.name)
        try:
            write_wav(wav_path, pcm)
            return self._transcribe_wav(wav_path)
        finally:
            try:
                wav_path.unlink(missing_ok=True)
            except Exception:
                pass

    def diarize_audio(self, source: str, pcm: bytes, timestamp_ms: int) -> None:
        if not pcm_has_voice(pcm):
            return

        self.ensure_loaded()
        speaker_id = self._speaker_for(source, Path(""), pcm, allow_fallback=False)
        if not speaker_id:
            return

        speaker_id = self._stable_segment_speaker(source, speaker_id)
        if not speaker_id:
            return

        self.last_emitted_segment_speakers[source] = speaker_id
        end_ms = timestamp_ms + int(len(pcm) / BYTES_PER_SECOND * 1000)
        emit({
            "type": "speaker_segment",
            "speakerId": speaker_id,
            "startMs": timestamp_ms,
            "endMs": end_ms,
            "confidence": diarization_turn_confidence(0.0, len(pcm) / BYTES_PER_SECOND),
        })

    def _stable_segment_speaker(self, source: str, speaker_id: str) -> str | None:
        if self.stable_segment_speakers.get(source) == speaker_id:
            return speaker_id

        if self.pending_segment_speakers.get(source) == speaker_id:
            self.pending_segment_counts[source] = self.pending_segment_counts.get(source, 1) + 1
        else:
            self.pending_segment_speakers[source] = speaker_id
            self.pending_segment_counts[source] = 1

        if self.pending_segment_counts[source] < STABLE_SPEAKER_WINDOWS:
            return None

        self.stable_segment_speakers[source] = speaker_id
        return speaker_id

    def _resolve_stt_device(self) -> str:
        if self.config.compute_mode == "cpu":
            return "cpu"
        if self.config.compute_mode == "cuda":
            return "cuda"
        return "cuda" if ctranslate2_cuda_available() else "cpu"

    def _resolve_torch_device(self) -> str:
        if self.config.compute_mode == "cpu":
            return "cpu"
        if self.config.compute_mode == "cuda":
            return "cuda" if torch_cuda_available() else "cpu"
        return "cuda" if torch_cuda_available() else "cpu"

    def _resolve_diarization_device(self) -> str:
        if self.config.diarization_model == "diart" and self.config.compute_mode == "auto":
            return "cpu"
        return self._resolve_torch_device()

    def _resolve_stt_compute_type(self, device: str) -> str:
        if device != "cuda":
            return "int8"
        return "float16" if self.config.stt_quality_preset >= 75 else "int8_float16"

    def transcribe_chunk_bytes(self) -> int:
        seconds = transcribe_chunk_seconds_for_quality(self.config.stt_quality_preset)
        if self._uses_qwen_asr() and self.config.diarization_enabled and not self._uses_streaming_diarization():
            seconds = max(seconds, QWEN_DIARIZED_CHUNK_SECONDS)
        return BYTES_PER_SECOND * seconds

    def diarization_stream_chunk_bytes(self) -> int:
        return int(BYTES_PER_SECOND * self._diart_step_seconds())

    def diarization_stream_chunk_seconds(self) -> float:
        return self._diart_step_seconds()

    def _transcribe_wav(self, wav_path: Path) -> str:
        return join_text_parts(self._transcribe_wav_parts(wav_path))

    def _transcribe_wav_parts(self, wav_path: Path) -> list[TimedTextPart]:
        if self._uses_qwen_asr():
            return self._transcribe_qwen_wav_parts(wav_path)

        if self._uses_whisperx_asr():
            return self._transcribe_whisperx_wav_parts(wav_path)

        if self.whisper_model:
            return self._transcribe_parts_with_language_filter(wav_path)

        if not self.warned_mock:
            self.warned_mock = True
            emit({"type": "model_status", "stage": "mock_mode", "message": "Install worker requirements to enable local STT.", "progress": None})
        return []

    def _transcribe_qwen_wav_parts(self, wav_path: Path) -> list[TimedTextPart]:
        if not self.qwen_model or self.qwen_model is False:
            return []
        if self.qwen_disabled_reason:
            return self._transcribe_qwen_fallback_or_stop(wav_path, self.qwen_disabled_reason)

        language = qwen_language_name(self._primary_language())
        return_time_stamps = language is None or qwen_forced_aligner_supports_language(language)
        parts = self._transcribe_qwen_wav_parts_once(wav_path, language, return_time_stamps)
        if parts is None:
            return self._transcribe_qwen_fallback_or_stop(wav_path, self.qwen_disabled_reason or "Qwen-ASR timed out.")
        if parts or not return_time_stamps:
            self._emit_qwen_alignment_debug(return_time_stamps, parts)
            return parts or self._transcribe_qwen_fallback_or_stop(wav_path)

        self._emit_qwen_empty_result_debug("ForcedAligner timestamp result was empty; retrying Qwen-ASR without timestamps.")
        retry_parts = self._transcribe_qwen_wav_parts_once(wav_path, language, False)
        if retry_parts is None:
            return self._transcribe_qwen_fallback_or_stop(wav_path, self.qwen_disabled_reason or "Qwen-ASR timed out.")
        if retry_parts:
            self._emit_qwen_alignment_debug(return_time_stamps, retry_parts)
            return retry_parts

        self._emit_qwen_alignment_debug(return_time_stamps, [])
        return self._transcribe_qwen_fallback_or_stop(wav_path)

    def _transcribe_qwen_wav_parts_once(self, wav_path: Path, language: str | None, return_time_stamps: bool) -> list[TimedTextPart] | None:
        results = self._call_qwen_transcribe_with_timeout(wav_path, language, return_time_stamps)
        if results is None:
            return None
        slow_seconds = qwen_slow_fallback_seconds()
        if self.last_qwen_call_seconds >= slow_seconds:
            self.qwen_disabled_reason = self._qwen_asr_failure_reason(f"Qwen-ASR too slow ({self.last_qwen_call_seconds:.1f}s >= {slow_seconds:g}s)")
            if not self.qwen_slow_debug_emitted:
                self.qwen_slow_debug_emitted = True
                emit({
                    "type": "model_status",
                    "stage": "qwen_slow",
                    "message": self.qwen_disabled_reason,
                    "progress": 0,
                })
            if not self._qwen_fallback_allowed():
                self._stop_for_qwen_asr_failure(self.qwen_disabled_reason)
            return None
        result = results[0] if isinstance(results, list) else results
        parts = timed_text_parts_for_qwen_result(result, wav_duration_ms(wav_path))
        result_language = str(getattr(result, "language", "") or language or "")
        return punctuate_qwen_text_parts(parts, result_language)

    def _call_qwen_transcribe_with_timeout(self, wav_path: Path, language: str | None, return_time_stamps: bool) -> Any | None:
        if threading.current_thread().name == ASR_THREAD_NAME:
            return self._call_qwen_transcribe_in_asr_worker(wav_path, language, return_time_stamps)

        timeout_seconds = qwen_timeout_seconds()
        result_box: dict[str, Any] = {}
        started = time.perf_counter()

        def run_transcribe() -> None:
            try:
                result_box["results"] = self.qwen_model.transcribe(
                    audio=str(wav_path),
                    context=qwen_transcription_context(),
                    language=language,
                    return_time_stamps=return_time_stamps,
                )
            except Exception as exc:
                result_box["error"] = exc

        thread = threading.Thread(target=run_transcribe, daemon=True)
        thread.start()
        thread.join(timeout_seconds)
        if thread.is_alive():
            self.last_qwen_call_seconds = timeout_seconds
            self.qwen_disabled_reason = self._qwen_asr_failure_reason(f"Qwen-ASR timed out after {timeout_seconds:g}s")
            if not self.qwen_timeout_debug_emitted:
                self.qwen_timeout_debug_emitted = True
                emit({
                    "type": "model_status",
                    "stage": "qwen_timeout",
                    "message": self.qwen_disabled_reason,
                    "progress": 0,
                })
            if not self._qwen_fallback_allowed():
                self._stop_for_qwen_asr_failure(self.qwen_disabled_reason)
            return None

        if "error" in result_box:
            raise result_box["error"]
        self.last_qwen_call_seconds = time.perf_counter() - started
        return result_box.get("results")

    def _call_qwen_transcribe_in_asr_worker(self, wav_path: Path, language: str | None, return_time_stamps: bool) -> Any | None:
        timeout_seconds = qwen_timeout_seconds()
        started = time.perf_counter()
        finished = threading.Event()
        timed_out = False

        def report_long_running_call() -> None:
            nonlocal timed_out
            if finished.is_set():
                return
            timed_out = True
            self.last_qwen_call_seconds = timeout_seconds
            self.qwen_disabled_reason = self._qwen_asr_failure_reason(f"Qwen-ASR timed out after {timeout_seconds:g}s")
            if not self.qwen_timeout_debug_emitted:
                self.qwen_timeout_debug_emitted = True
                emit({
                    "type": "model_status",
                    "stage": "qwen_timeout",
                    "message": self.qwen_disabled_reason,
                    "progress": 0,
                })
            if not self.qwen_running_debug_emitted:
                self.qwen_running_debug_emitted = True
                emit({
                    "type": "model_status",
                    "stage": "qwen_still_running",
                    "message": "Qwen-ASR is still inside the model call. Audio capture continues, but captions wait for this ASR chunk to return.",
                    "progress": 0,
                })

        timer = threading.Timer(timeout_seconds, report_long_running_call)
        timer.daemon = True
        timer.start()
        try:
            results = self.qwen_model.transcribe(
                audio=str(wav_path),
                context=qwen_transcription_context(),
                language=language,
                return_time_stamps=return_time_stamps,
            )
        finally:
            finished.set()
            timer.cancel()

        self.last_qwen_call_seconds = time.perf_counter() - started
        if timed_out:
            return None
        return results

    def _transcribe_qwen_fallback_or_stop(self, wav_path: Path, reason: str | None = None) -> list[TimedTextPart]:
        if not self._qwen_fallback_allowed():
            self._stop_for_qwen_asr_failure(reason or "Qwen-ASR returned no text.")
        return self._transcribe_qwen_fallback_whisper_parts(wav_path, reason)

    def _qwen_fallback_allowed(self) -> bool:
        return True

    def _qwen_asr_failure_reason(self, reason: str) -> str:
        return f"{reason}; using faster-whisper fallback."

    def _stop_for_qwen_asr_failure(self, reason: str) -> None:
        message = f"{reason} Stopping worker."
        emit({"type": "error", "code": "qwen_asr_failed", "message": message, "recoverable": False})
        raise WorkerStopRequested(message)

    def _transcribe_qwen_fallback_whisper_parts(self, wav_path: Path, reason: str | None = None) -> list[TimedTextPart]:
        model = self._ensure_qwen_fallback_whisper_model()
        if not model:
            return []

        if not self.qwen_fallback_debug_emitted:
            self.qwen_fallback_debug_emitted = True
            emit({
                "type": "model_status",
                "stage": "qwen_fallback_whisper",
                "message": reason or "Qwen-ASR returned no text; using faster-whisper fallback for this chunk.",
                "progress": 0,
            })

        parts = timed_text_parts_for_segments(self._transcribe_segments_with_model_language_filter(wav_path, model))
        if parts:
            return parts

        if not self.qwen_fallback_retry_debug_emitted:
            self.qwen_fallback_retry_debug_emitted = True
            emit({
                "type": "model_status",
                "stage": "qwen_fallback_retry",
                "message": "faster-whisper fallback returned no text; retrying with relaxed VAD and thresholds.",
                "progress": 0,
            })
        return timed_text_parts_for_segments(
            self._transcribe_segments_with_model_language_filter(
                wav_path,
                model,
                relaxed_whisper_transcribe_options(self.config.stt_quality_preset),
            )
        )

    def _ensure_qwen_fallback_whisper_model(self) -> Any:
        if self.qwen_fallback_whisper_model is False:
            return None
        if self.qwen_fallback_whisper_model is not None:
            return self.qwen_fallback_whisper_model

        try:
            from faster_whisper import WhisperModel

            model_name = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_FALLBACK_WHISPER_MODEL", "large-v3-turbo")
            device = self._resolve_stt_device()
            compute_type = self._resolve_stt_compute_type(device)
            self.qwen_fallback_whisper_model = WhisperModel(
                model_name,
                device=device,
                compute_type=compute_type,
                download_root=str(self.models_dir / "whisper"),
            )
            emit({
                "type": "model_status",
                "stage": "qwen_fallback_loaded",
                "message": f"faster-whisper fallback {model_name} on {device}",
                "progress": 1,
            })
            return self.qwen_fallback_whisper_model
        except Exception as exc:
            self.qwen_fallback_whisper_model = False
            emit({"type": "error", "code": "qwen_fallback_unavailable", "message": str(exc), "recoverable": True})
            return None

    def _emit_qwen_empty_result_debug(self, message: str) -> None:
        if self.qwen_empty_result_debug_emitted:
            return

        self.qwen_empty_result_debug_emitted = True
        emit({
            "type": "model_status",
            "stage": "qwen_retry_plain_text",
            "message": message,
            "progress": 0,
        })

    def _transcribe_whisperx_wav_parts(self, wav_path: Path) -> list[TimedTextPart]:
        if not self.whisperx_engine or self.whisperx_engine is False:
            return []

        return transcribe_with_whisperx(
            self.whisperx_engine,
            wav_path,
            self.config.stt_languages,
            self.config.stt_quality_preset,
        )

    def _emit_qwen_alignment_debug(self, requested_timestamps: bool, parts: list[TimedTextPart]) -> None:
        if self.qwen_alignment_debug_emitted:
            return

        self.qwen_alignment_debug_emitted = True
        timed_parts = [part for part in parts if part.has_word_timing]
        if timed_parts:
            emit({
                "type": "model_status",
                "stage": "qwen_alignment",
                "message": f"ForcedAligner timestamps on: parts={len(timed_parts)}, span={timed_parts[0].start_ms}-{timed_parts[-1].end_ms} ms",
                "progress": 1,
            })
            return

        emit({
            "type": "model_status",
            "stage": "qwen_alignment",
            "message": f"ForcedAligner timestamps {'requested but missing' if requested_timestamps else 'off'}; using caption-level timing",
            "progress": 0,
        })

    def _try_transcribe_whisperlivekit_sortformer(self, source: str, pcm: bytes, timestamp_ms: int, started: float) -> bool:
        if source == "mic" or not self.whisperlivekit_engine or self.whisperlivekit_engine is False:
            return True

        stt_audio = preprocess_stt_pcm(pcm)
        if stt_audio is None and self.whisperlivekit_session is None:
            return True

        try:
            session = self._whisperlivekit_streaming_session()
            events = session.transcribe_pcm(
                pcm,
                timestamp_ms,
                int((time.perf_counter() - started) * 1000),
            )
        except Exception as exc:
            emit({"type": "error", "code": "whisperlivekit_failed", "message": str(exc), "recoverable": True})
            return True

        if not events:
            return True

        if self.config.diarization_enabled and self._uses_streaming_diarization():
            self._apply_cached_diarization_speakers_to_events(source, events)
        elif not self._uses_whisperlivekit_sortformer():
            raw_end_ms = timestamp_ms + int(len(pcm) / BYTES_PER_SECOND * 1000)
            start_ms = timestamp_ms + (stt_audio.leading_trim_ms if stt_audio else 0)
            end_ms = max(start_ms + 1, raw_end_ms - (stt_audio.trailing_trim_ms if stt_audio else 0))
            speaker_id = (
                self._cached_diarization_speaker_for(source, start_ms, end_ms)
                if self._uses_streaming_diarization()
                else self._speaker_for(source, Path(""), pcm)
            ) or self._fallback_speaker_id(source)
            for event in events:
                event["speakerId"] = speaker_id

        for event in events:
            emit(event)
        return True

    def _apply_cached_diarization_speakers_to_events(self, source: str, events: list[dict[str, Any]]) -> None:
        for event in events:
            try:
                start_ms = int(event.get("startMs", 0))
                end_ms = int(event.get("endMs", start_ms + 1))
            except (TypeError, ValueError):
                continue
            speaker_id = self._cached_diarization_speaker_for(source, start_ms, end_ms, allow_fallback=True)
            if speaker_id:
                event["speakerId"] = speaker_id

    def _transcribe_with_language_filter(self, wav_path: Path) -> str:
        return join_text_parts(self._transcribe_parts_with_language_filter(wav_path))

    def _transcribe_parts_with_language_filter(self, wav_path: Path) -> list[TimedTextPart]:
        return timed_text_parts_for_segments(self._transcribe_segments_with_language_filter(wav_path))

    def _transcribe_segments_with_language_filter(self, wav_path: Path) -> list[Any]:
        return self._transcribe_segments_with_model_language_filter(wav_path, self.whisper_model)

    def _transcribe_segments_with_model_language_filter(self, wav_path: Path, model: Any, options: dict[str, Any] | None = None) -> list[Any]:
        allowed_languages = self.config.stt_languages
        options = dict(options or whisper_transcribe_options(self.config.stt_quality_preset))
        if len(allowed_languages) == 1:
            segments, _info = model.transcribe(
                str(wav_path),
                language=allowed_languages[0],
                **options,
            )
            return list(segments)

        segments, info = model.transcribe(
            str(wav_path),
            language=None,
            **options,
        )
        segments = list(segments)
        detected_language = str(getattr(info, "language", "") or "").lower()
        if not allowed_languages or detected_language in allowed_languages:
            return segments

        segments, _info = model.transcribe(
            str(wav_path),
            language=allowed_languages[0],
            **options,
        )
        return list(segments)

    def _speaker_for(self, source: str, wav_path: Path, pcm: bytes, allow_fallback: bool = True) -> str | None:
        if source == "mic":
            return "mic"

        if self.diarization_pipeline:
            try:
                context_pcm = self._diarization_window_for(source, pcm)
                if len(context_pcm) < DIARIZATION_MIN_CONTEXT_BYTES:
                    return self._fallback_speaker_id(source) if allow_fallback else None

                context_seconds = len(context_pcm) / BYTES_PER_SECOND
                diarization = self.diarization_pipeline(
                    {"waveform": pcm_to_waveform(context_pcm), "sample_rate": SAMPLE_RATE},
                    **self._diarization_speaker_kwargs(context_seconds),
                )
                label = select_current_speaker_label(
                    diarization,
                    context_seconds=context_seconds,
                    current_seconds=len(pcm) / BYTES_PER_SECOND,
                )
                if label:
                    speaker_key = self._stable_speaker_key(diarization, label)
                    if speaker_key not in self.speaker_ids:
                        if len(self.speaker_ids) >= max(1, self.config.max_speakers):
                            return self._fallback_speaker_id(source)
                        self.speaker_ids[speaker_key] = f"speaker_{len(self.speaker_ids) + 1}"
                    return self.speaker_ids[speaker_key]
            except Exception as exc:
                emit({"type": "error", "code": "diarization_failed", "message": str(exc), "recoverable": True})

        return self._fallback_speaker_id(source) if allow_fallback else None

    def _uses_streaming_diarization(self) -> bool:
        return self.config.diarization_model in {"diart", "sortformer"}

    def _uses_qwen_asr(self) -> bool:
        return self.config.asr_engine == "qwen3_asr_diarization"

    def _uses_whisperlivekit_asr(self) -> bool:
        return self.config.asr_engine == "whisperlivekit_sortformer"

    def _uses_whisperx_asr(self) -> bool:
        return self.config.asr_engine == "whisperx"

    def _uses_whisperlivekit_sortformer(self) -> bool:
        return self._uses_whisperlivekit_asr() and self.config.diarization_enabled and self.config.diarization_model == "sortformer"

    def _primary_language(self) -> str | None:
        return self.config.stt_languages[0] if self.config.stt_languages else None

    def _emit_text_parts_for_speaker_turns(
        self,
        text_parts: list[TimedTextPart],
        speaker_turns: list[tuple[int, int, str]],
        base_start_ms: int,
        fallback_speaker_id: str,
        latency_ms: int,
    ) -> bool:
        groups: list[dict[str, Any]] = []
        current_speaker_id: str | None = None
        for part in text_parts:
            part_start_ms = base_start_ms + part.start_ms
            part_end_ms = max(part_start_ms + 1, base_start_ms + part.end_ms)
            speaker_id = (
                speaker_for_time_range(speaker_turns, part_start_ms, part_end_ms)
                or current_speaker_id
                or fallback_speaker_id
            )
            current_speaker_id = speaker_id

            if groups and groups[-1]["speakerId"] == speaker_id:
                groups[-1]["parts"].append(part.text)
                groups[-1]["endMs"] = part_end_ms
                continue

            groups.append({
                "speakerId": speaker_id,
                "parts": [part.text],
                "startMs": part_start_ms,
                "endMs": part_end_ms,
            })

        emitted = False
        for group in groups:
            text = filter_transcribed_caption_text("".join(group["parts"]))
            if not text:
                continue

            emit({
                "type": "final_caption",
                "speakerId": group["speakerId"],
                "text": text,
                "startMs": group["startMs"],
                "endMs": max(group["startMs"] + 1, group["endMs"]),
                "latencyMs": latency_ms,
            })
            emitted = True

        return emitted

    def _emit_streaming_diarized_text_parts(
        self,
        source: str,
        text_parts: list[TimedTextPart],
        base_start_ms: int,
        fallback_speaker_id: str,
        latency_ms: int,
    ) -> bool:
        if (
            not self._uses_streaming_diarization()
            or not text_parts
            or not all(part.has_word_timing for part in text_parts)
        ):
            return False

        groups: list[dict[str, Any]] = []
        current_speaker_id: str | None = None
        for part in text_parts:
            part_start_ms = base_start_ms + part.start_ms
            part_end_ms = max(part_start_ms + 1, base_start_ms + part.end_ms)
            speaker_id = (
                self._cached_diarization_speaker_for(source, part_start_ms, part_end_ms, allow_fallback=False)
                or current_speaker_id
                or fallback_speaker_id
            )
            current_speaker_id = speaker_id

            if groups and groups[-1]["speakerId"] == speaker_id:
                groups[-1]["parts"].append(part.text)
                groups[-1]["endMs"] = part_end_ms
                continue

            groups.append({
                "speakerId": speaker_id,
                "parts": [part.text],
                "startMs": part_start_ms,
                "endMs": part_end_ms,
            })

        if len(groups) <= 1:
            return False

        emitted = False
        for group in groups:
            text = filter_transcribed_caption_text("".join(group["parts"]))
            if not text:
                continue

            emit({
                "type": "final_caption",
                "speakerId": group["speakerId"],
                "text": text,
                "startMs": group["startMs"],
                "endMs": max(group["startMs"] + 1, group["endMs"]),
                "latencyMs": latency_ms,
            })
            emitted = True

        return emitted

    def _cached_diarization_speaker_for(
        self,
        source: str,
        start_ms: int | None = None,
        end_ms: int | None = None,
        allow_fallback: bool = True,
    ) -> str | None:
        with self.streaming_diarization_lock:
            if start_ms is not None and end_ms is not None:
                overlap_by_speaker: dict[str, int] = {}
                for segment_start_ms, segment_end_ms, speaker_id in self.streaming_diarization_segments.get(source, []):
                    overlap = min(end_ms, segment_end_ms) - max(start_ms, segment_start_ms)
                    if overlap > 0:
                        overlap_by_speaker[speaker_id] = overlap_by_speaker.get(speaker_id, 0) + overlap

                if overlap_by_speaker:
                    speaker_id, overlap = max(overlap_by_speaker.items(), key=lambda item: (item[1], item[0]))
                    caption_duration = max(1, end_ms - start_ms)
                    if overlap / caption_duration >= DIART_MIN_CAPTION_COVERAGE_RATIO:
                        return speaker_id

            return self.last_diarization_speakers.get(source) if allow_fallback else None

    def queue_streaming_diarization(self, source: str, pcm: bytes, timestamp_ms: int = 0) -> None:
        if source == "mic" or not self.diarization_pipeline or not pcm_has_voice(pcm):
            return

        with self.streaming_diarization_lock:
            if self.streaming_diarization_busy:
                pending = self.streaming_diarization_pending.setdefault(source, [timestamp_ms, bytearray()])
                pending[1].extend(pcm)
                max_bytes = BYTES_PER_SECOND * DIART_PENDING_MAX_SECONDS
                if len(pending[1]) > max_bytes:
                    trim_bytes = len(pending[1]) - max_bytes
                    del pending[1][:trim_bytes]
                    pending[0] = int(pending[0]) + int(trim_bytes / BYTES_PER_SECOND * 1000)
                return
            self.streaming_diarization_busy = True
            generation = self.streaming_diarization_generation

        thread = threading.Thread(
            target=self._run_streaming_diarization_loop,
            args=(source, pcm, timestamp_ms, generation),
            daemon=True,
        )
        with self.streaming_diarization_lock:
            self.streaming_diarization_thread = thread
        thread.start()

    def _run_streaming_diarization_loop(self, source: str, pcm: bytes, timestamp_ms: int, generation: int) -> None:
        try:
            next_pcm = pcm
            next_timestamp_ms = timestamp_ms
            while next_pcm:
                self._update_streaming_diarization(source, next_pcm, next_timestamp_ms, generation)
                with self.streaming_diarization_lock:
                    pending = self.streaming_diarization_pending.get(source)
                    if not pending:
                        break
                    next_timestamp_ms = int(pending[0])
                    next_pcm = bytes(pending[1])
                    self.streaming_diarization_pending.pop(source, None)
        finally:
            with self.streaming_diarization_lock:
                self.streaming_diarization_busy = False

    def _update_streaming_diarization(self, source: str, pcm: bytes, timestamp_ms: int, generation: int) -> None:
        current_seconds = len(pcm) / BYTES_PER_SECOND
        if current_seconds <= 0:
            return

        try:
            with self.streaming_diarization_lock:
                base_timestamp_ms = self._streaming_diarization_base_timestamp_ms(source, timestamp_ms)
            diarization = self.diarization_pipeline(
                {"waveform": pcm_to_waveform(pcm), "sample_rate": SAMPLE_RATE},
                **self._diarization_speaker_kwargs(current_seconds),
            )
            turns = exclusive_diarization_turns(diarization_turns_for_window(
                diarization,
                window_start_seconds=0.0,
                window_end_seconds=float("inf"),
            ))
            if not turns:
                return

            label = turns[-1][0]
            selected_speaker_id = self._speaker_id_for_label(diarization, label, source)
            for turn_label, start_seconds, end_seconds in turns:
                speaker_id = self._speaker_id_for_label(diarization, turn_label, source)
                start_ms = base_timestamp_ms + int(start_seconds * 1000)
                end_ms = base_timestamp_ms + int(end_seconds * 1000)
                emit({
                    "type": "speaker_segment",
                    "speakerId": speaker_id,
                    "startMs": start_ms,
                    "endMs": end_ms,
                    "confidence": diarization_turn_confidence(start_seconds, end_seconds, current_seconds),
                })
                with self.streaming_diarization_lock:
                    if generation == self.streaming_diarization_generation:
                        segments = self.streaming_diarization_segments.setdefault(source, [])
                        segments.append((start_ms, end_ms, speaker_id))
                        if len(segments) > 400:
                            del segments[:-400]

            with self.streaming_diarization_lock:
                if generation == self.streaming_diarization_generation:
                    self.last_diarization_speakers[source] = selected_speaker_id
        except Exception as exc:
            emit({"type": "error", "code": "diarization_failed", "message": str(exc), "recoverable": True})

    def _streaming_diarization_base_timestamp_ms(self, source: str, timestamp_ms: int) -> int:
        if self._streaming_diarization_uses_absolute_time():
            return self.streaming_diarization_start_ms.setdefault(source, timestamp_ms)
        return timestamp_ms

    def _streaming_diarization_uses_absolute_time(self) -> bool:
        if self.diarization_pipeline is not None:
            absolute = getattr(self.diarization_pipeline, "outputs_absolute_time", None)
            if absolute is not None:
                return bool(absolute)
        return True

    def _speaker_id_for_label(self, diarization: Any, label: str, source: str) -> str:
        with self.streaming_diarization_lock:
            speaker_key = self._stable_speaker_key(diarization, label)
            if speaker_key not in self.speaker_ids:
                if len(self.speaker_ids) >= max(1, self.config.max_speakers):
                    return self._fallback_speaker_id(source)
                self.speaker_ids[speaker_key] = f"speaker_{len(self.speaker_ids) + 1}"
            return self.speaker_ids[speaker_key]

    def _diarization_speaker_kwargs(self, context_seconds: float) -> dict[str, int]:
        if self.config.exact_speakers:
            exact_speakers = max(1, self.config.exact_speakers)
            return {"min_speakers": 1, "max_speakers": exact_speakers}
        return {"min_speakers": 1, "max_speakers": max(1, self.config.max_speakers)}

    def _fallback_speaker_id(self, source: str) -> str:
        return (
            self.stable_segment_speakers.get(source)
            or self.last_emitted_segment_speakers.get(source)
            or next(iter(self.speaker_ids.values()), None)
            or "speaker_1"
        )

    def _stable_speaker_key(self, diarization: Any, label: str) -> str:
        embedding = speaker_embedding_for_label(diarization, label)
        if embedding is None:
            return f"label:{label}"

        import numpy as np

        vector = np.asarray(embedding, dtype="float32")
        if vector.size == 0 or not np.all(np.isfinite(vector)):
            return f"label:{label}"

        norm = float(np.linalg.norm(vector))
        if norm <= 0:
            return f"label:{label}"

        best_key = None
        best_score = -1.0
        for key, known in self.speaker_embeddings.items():
            known_vector = np.asarray(known, dtype="float32")
            known_norm = float(np.linalg.norm(known_vector))
            if known_norm <= 0:
                continue
            score = float(np.dot(vector, known_vector) / (norm * known_norm))
            if score > best_score:
                best_key = key
                best_score = score

        if best_key is not None and best_score >= DIARIZATION_EMBEDDING_MATCH_THRESHOLD:
            self.speaker_embeddings[best_key] = (
                np.asarray(self.speaker_embeddings[best_key], dtype="float32") * (1.0 - DIARIZATION_EMBEDDING_UPDATE_RATE)
            ) + (vector * DIARIZATION_EMBEDDING_UPDATE_RATE)
            return best_key

        key = f"embedding:{len(self.speaker_embeddings) + 1}"
        self.speaker_embeddings[key] = vector
        return key

    def _diarization_context_for(self, source: str, pcm: bytes) -> bytes:
        buffer = self.diarization_buffers.setdefault(source, bytearray())
        buffer.extend(pcm)
        context_bytes = self._diarization_context_bytes()
        if len(buffer) > context_bytes:
            del buffer[:-context_bytes]
        return bytes(buffer)

    def _diarization_window_for(self, source: str, pcm: bytes) -> bytes:
        if self._uses_streaming_diarization():
            return pcm
        return self._diarization_context_for(source, pcm)

    def _diarization_context_seconds(self) -> int:
        return diarization_context_seconds_for_quality(self.config.diarization_quality_preset)

    def _diarization_context_bytes(self) -> int:
        return BYTES_PER_SECOND * self._diarization_context_seconds()

    def _diart_duration_seconds(self) -> float:
        return self.config.diart_duration_seconds if self.config.diart_manual_settings else diart_duration_seconds_for_quality(self.config.diarization_quality_preset)

    def _diart_step_seconds(self) -> float:
        return self.config.diart_step_seconds if self.config.diart_manual_settings else DIART_STREAM_CHUNK_SECONDS

    def _diart_latency_seconds(self) -> float:
        return self.config.diart_latency_seconds if self.config.diart_manual_settings else diart_latency_seconds_for_quality(self.config.diarization_quality_preset)

    def _diart_hyper_parameters(self) -> dict[str, float]:
        if not self.config.diart_manual_settings:
            return diart_hyper_parameters_for_quality(self.config.diarization_quality_preset)
        return {
            "tau_active": self.config.diart_tau_active,
            "rho_update": self.config.diart_rho_update,
            "delta_new": self.config.diart_delta_new,
        }


def ctranslate2_cuda_available() -> bool:
    try:
        import ctranslate2

        return bool(ctranslate2.get_supported_compute_types("cuda"))
    except Exception:
        return False


def torch_cuda_available() -> bool:
    try:
        import torch

        return bool(torch.cuda.is_available())
    except Exception:
        return False


class StreamWorker:
    def __init__(self, models_dir: Path) -> None:
        self.engine = LocalSpeechEngine(models_dir)
        self.running = False
        self.buffers: dict[str, bytearray] = {"system": bytearray(), "mic": bytearray()}
        self.first_ts: dict[str, int] = {"system": 0, "mic": 0}
        self.diarization_buffers: dict[str, bytearray] = {"system": bytearray(), "mic": bytearray()}
        self.diarization_first_ts: dict[str, int] = {"system": 0, "mic": 0}
        self.transcription_queue: queue.Queue[tuple[int, str, bytes, int, bool]] = queue.Queue()
        self.transcription_thread: threading.Thread | None = None
        self.transcription_thread_generation: int | None = None
        self.transcription_active = False
        self.transcription_active_generation: int | None = None
        self.transcription_lock = threading.RLock()
        self.transcription_generation = 0

    def handle(self, message: dict[str, Any]) -> None:
        message_type = message.get("type")
        if message_type == "configure":
            self._invalidate_transcription_queue()
            self.engine.configure(message)
        elif message_type == "start":
            self._start()
        elif message_type == "stop":
            self.running = False
            self._clear_audio_buffers()
            self._invalidate_transcription_queue()
            self.engine.close_whisperlivekit_session()
            emit({"type": "model_status", "stage": "stopped", "message": "worker stopped", "progress": None})
        elif message_type == "audio_chunk" and self.running:
            try:
                self._handle_audio(message)
            except WorkerStopRequested as exc:
                self.running = False
                self._clear_audio_buffers()
                self.engine.close_whisperlivekit_session()
                emit({"type": "model_status", "stage": "stopped", "message": str(exc), "progress": None})

    def _start(self) -> None:
        self.running = False
        self._clear_audio_buffers()
        self._invalidate_transcription_queue()
        emit({"type": "model_status", "stage": "loading_models", "message": "loading STT and diarization models", "progress": None})
        self.engine.ensure_loaded()
        if not self._models_ready():
            details = "; ".join(
                message
                for message in [self.engine.last_stt_error, self.engine.last_diarization_error]
                if message
            )
            emit({
                "type": "model_status",
                "stage": "setup_failed",
                "message": details or "model loading failed",
                "progress": None,
            })
            return

        self.running = True
        emit({"type": "model_status", "stage": "listening", "message": "worker started", "progress": None})

    def _models_ready(self) -> bool:
        if not self.engine.whisper_model:
            return False
        if self.engine.config.diarization_enabled:
            return bool(self.engine.diarization_pipeline)
        return True

    def _clear_audio_buffers(self) -> None:
        for buffer in self.buffers.values():
            buffer.clear()
        for buffer in self.diarization_buffers.values():
            buffer.clear()

    def _handle_audio(self, message: dict[str, Any]) -> None:
        source = message.get("source", "system")
        data = base64.b64decode(message.get("data", ""))
        timestamp_ms = int(message.get("timestampMs", 0))
        if source not in self.buffers:
            self.buffers[source] = bytearray()
            self.first_ts[source] = timestamp_ms
        if not self.buffers[source]:
            self.first_ts[source] = timestamp_ms
        self.buffers[source].extend(data)
        self._handle_streaming_diarization_audio(source, data, timestamp_ms)

        if len(self.buffers[source]) >= self.engine.transcribe_chunk_bytes():
            pcm = bytes(self.buffers[source])
            self.buffers[source].clear()
            self._enqueue_transcription(
                source,
                pcm,
                self.first_ts[source],
                queue_diarization=not self.engine._uses_streaming_diarization(),
            )

    def _enqueue_transcription(self, source: str, pcm: bytes, timestamp_ms: int, queue_diarization: bool) -> None:
        with self.transcription_lock:
            generation = self.transcription_generation
            self.transcription_queue.put((generation, source, pcm, timestamp_ms, queue_diarization))
            emit({
                "type": "model_status",
                "stage": "asr_queued",
                "message": f"ASR queued source={source}, start={timestamp_ms}ms, duration={int(len(pcm) / BYTES_PER_SECOND * 1000)}ms, queue={self.transcription_queue.qsize()}",
                "progress": None,
            })
            if (
                self.transcription_thread is not None
                and self.transcription_thread.is_alive()
                and self.transcription_thread_generation == generation
                and self.transcription_active
                and self.transcription_active_generation == generation
            ):
                return

            thread = threading.Thread(target=self._run_transcription_loop, args=(generation,), name=ASR_THREAD_NAME, daemon=True)
            self.transcription_thread = thread
            self.transcription_thread_generation = generation
            thread.start()

    def _run_transcription_loop(self, worker_generation: int) -> None:
        current_thread = threading.current_thread()
        try:
            while True:
                try:
                    generation, source, pcm, timestamp_ms, queue_diarization = self.transcription_queue.get_nowait()
                except queue.Empty:
                    with self.transcription_lock:
                        if self.transcription_queue.empty():
                            return
                    continue

                if generation != worker_generation:
                    with self.transcription_lock:
                        if generation == self.transcription_generation and self.running:
                            self.transcription_queue.put((generation, source, pcm, timestamp_ms, queue_diarization))
                    return

                with self.transcription_lock:
                    stale = generation != self.transcription_generation or not self.running
                if stale:
                    continue

                duration_ms = int(len(pcm) / BYTES_PER_SECOND * 1000)
                with self.transcription_lock:
                    self.transcription_active = True
                    self.transcription_active_generation = worker_generation
                emit({
                    "type": "model_status",
                    "stage": "asr_started",
                    "message": f"ASR started source={source}, start={timestamp_ms}ms, duration={duration_ms}ms",
                    "progress": None,
                })
                finished = threading.Event()
                long_running_seconds = self._asr_long_running_seconds()

                def report_long_running_call() -> None:
                    if finished.is_set():
                        return
                    emit({
                        "type": "model_status",
                        "stage": "asr_still_running",
                        "message": f"ASR still running after {long_running_seconds:g}s source={source}, start={timestamp_ms}ms, duration={duration_ms}ms",
                        "progress": 0,
                    })

                timer = threading.Timer(long_running_seconds, report_long_running_call)
                timer.daemon = True
                timer.start()
                try:
                    self.engine.transcribe(source, pcm, timestamp_ms, queue_diarization=queue_diarization)
                    with self.transcription_lock:
                        current_generation = worker_generation == self.transcription_generation and self.running
                    if current_generation:
                        emit({
                            "type": "model_status",
                            "stage": "asr_finished",
                            "message": f"ASR finished source={source}, start={timestamp_ms}ms, duration={duration_ms}ms",
                            "progress": 1,
                        })
                except WorkerStopRequested as exc:
                    already_stopped = not self.running
                    self.running = False
                    self._clear_audio_buffers()
                    self._invalidate_transcription_queue()
                    self.engine.close_whisperlivekit_session()
                    if not already_stopped:
                        emit({"type": "model_status", "stage": "stopped", "message": str(exc), "progress": None})
                    return
                except Exception as exc:
                    emit({"type": "error", "code": "worker_exception", "message": str(exc), "recoverable": True})
                finally:
                    finished.set()
                    timer.cancel()
                    with self.transcription_lock:
                        if self.transcription_active_generation == worker_generation:
                            self.transcription_active = False
                            self.transcription_active_generation = None
        finally:
            with self.transcription_lock:
                if self.transcription_thread is current_thread:
                    self.transcription_thread = None
                    self.transcription_thread_generation = None
                    if self.transcription_active_generation == worker_generation:
                        self.transcription_active = False
                        self.transcription_active_generation = None

    def _invalidate_transcription_queue(self) -> None:
        with self.transcription_lock:
            abandoned_thread = self.transcription_thread is not None and self.transcription_thread.is_alive()
            self.transcription_generation += 1
            while True:
                try:
                    self.transcription_queue.get_nowait()
                except queue.Empty:
                    break
            if abandoned_thread:
                emit({
                    "type": "model_status",
                    "stage": "asr_generation_reset",
                    "message": "ASR generation reset while a previous ASR call is still running; new audio will use a fresh ASR worker.",
                    "progress": 0,
                })
            self.transcription_thread = None
            self.transcription_thread_generation = None
            self.transcription_active = False
            self.transcription_active_generation = None

    def _asr_long_running_seconds(self) -> float:
        return asr_long_running_seconds()

    def _handle_streaming_diarization_audio(self, source: str, data: bytes, timestamp_ms: int) -> None:
        if not self.engine._uses_streaming_diarization():
            return

        if source not in self.diarization_buffers:
            self.diarization_buffers[source] = bytearray()
            self.diarization_first_ts[source] = timestamp_ms

        buffer = self.diarization_buffers[source]
        if not buffer:
            self.diarization_first_ts[source] = timestamp_ms
        buffer.extend(data)
        chunk_bytes = self.engine.diarization_stream_chunk_bytes()
        chunk_ms = int(self.engine.diarization_stream_chunk_seconds() * 1000)
        while len(buffer) >= chunk_bytes:
            pcm = bytes(buffer[:chunk_bytes])
            del buffer[:chunk_bytes]
            chunk_timestamp_ms = self.diarization_first_ts[source]
            self.engine.queue_streaming_diarization(source, pcm, chunk_timestamp_ms)
            self.diarization_first_ts[source] = chunk_timestamp_ms + chunk_ms


def qwen_asr_model_id(model_name: str) -> str:
    normalized = str(model_name or "").strip().lower()
    if normalized in {"qwen3-asr-0.6b", "qwen/qwen3-asr-0.6b", "0.6b"}:
        return os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_ASR_06B", "Qwen/Qwen3-ASR-0.6B")
    if normalized in {"qwen3-asr-1.7b", "qwen/qwen3-asr-1.7b", "1.7b"}:
        return os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_ASR_17B", "Qwen/Qwen3-ASR-1.7B")
    return os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_ASR_17B", "Qwen/Qwen3-ASR-1.7B")


def load_qwen_asr_model(model_name: str, device: str) -> Any:
    import torch
    from qwen_asr import Qwen3ASRModel

    use_aligner = qwen_forced_aligner_enabled()
    kwargs: dict[str, Any] = {
        "dtype": torch.bfloat16 if device == "cuda" else torch.float32,
        "device_map": "cuda:0" if device == "cuda" else "cpu",
        "attn_implementation": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_ATTENTION", "sdpa"),
        "max_inference_batch_size": 4,
        "max_new_tokens": 512,
    }
    if use_aligner:
        kwargs["forced_aligner"] = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_FORCED_ALIGNER", "Qwen/Qwen3-ForcedAligner-0.6B")
        kwargs["forced_aligner_kwargs"] = {
            "device_map": kwargs["device_map"],
            "dtype": kwargs["dtype"],
            "attn_implementation": kwargs["attn_implementation"],
        }

    return Qwen3ASRModel.from_pretrained(qwen_asr_model_id(model_name), **kwargs)


def qwen_forced_aligner_enabled() -> bool:
    return os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_USE_ALIGNER", "true").lower() in {"1", "true", "yes", "on"}


def qwen_timeout_seconds() -> float:
    try:
        return max(0.01, float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_TIMEOUT_SECONDS", "4.0")))
    except ValueError:
        return 4.0


def asr_long_running_seconds() -> float:
    try:
        return max(0.1, float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_LONG_RUNNING_SECONDS", "6.0")))
    except ValueError:
        return 6.0


def qwen_slow_fallback_seconds() -> float:
    try:
        return max(0.01, float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_SLOW_FALLBACK_SECONDS", "2.5")))
    except ValueError:
        return 2.5


def qwen_language_name(language_code: str | None) -> str | None:
    if not language_code:
        return None
    mapping = {
        "ko": "Korean",
        "en": "English",
        "ja": "Japanese",
        "zh": "Chinese",
        "fr": "French",
        "de": "German",
        "es": "Spanish",
        "pt": "Portuguese",
        "ru": "Russian",
        "th": "Thai",
        "tr": "Turkish",
        "ar": "Arabic",
        "vi": "Vietnamese",
    }
    return mapping.get(language_code.lower(), language_code)


def qwen_forced_aligner_supports_language(language: str) -> bool:
    return qwen_language_name(language).lower() in QWEN_FORCED_ALIGNER_LANGUAGES


def qwen_transcription_context() -> str:
    return os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_CONTEXT", QWEN_TRANSCRIPTION_CONTEXT).strip()


def qwen_auto_punctuation_enabled() -> bool:
    value = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_QWEN_AUTO_PUNCTUATION", "1").strip().lower()
    return value not in {"0", "false", "off", "no"}


def timed_text_parts_for_qwen_result(result: Any, fallback_duration_ms: int) -> list[TimedTextPart]:
    stamps = getattr(result, "time_stamps", None) or getattr(result, "timestamps", None)
    parts = timed_text_parts_from_timestamp_items(stamps)
    if parts:
        return restore_qwen_timestamp_spacing(parts, str(getattr(result, "text", "") or ""))

    text = normalize_transcribed_text(str(getattr(result, "text", "") or ""))
    return [TimedTextPart(text, 0, max(1, fallback_duration_ms), False)] if text else []


def punctuate_qwen_text_parts(parts: list[TimedTextPart], language: str | None) -> list[TimedTextPart]:
    if not qwen_auto_punctuation_enabled() or not parts:
        return parts

    punctuated: list[TimedTextPart] = []
    for index, part in enumerate(parts):
        text = part.text
        next_part = parts[index + 1] if index + 1 < len(parts) else None
        if should_append_qwen_pause_terminator(part, next_part):
            text = append_qwen_sentence_terminator(text, language)
        punctuated.append(TimedTextPart(text, part.start_ms, part.end_ms, part.has_word_timing))

    return punctuated


def should_append_qwen_pause_terminator(part: TimedTextPart, next_part: TimedTextPart | None) -> bool:
    if not part.text.strip() or next_part is None:
        return False

    if not part.has_word_timing or not next_part.has_word_timing:
        return False

    # Only synthesize punctuation at a measured pause inside the caption.
    # The final token is preserved verbatim because appending a period there
    # can change translation segmentation and create wrong translations.
    return next_part.start_ms - part.end_ms >= QWEN_SENTENCE_PAUSE_MS


def append_qwen_sentence_terminator(text: str, language: str | None) -> str:
    if not text.strip() or has_terminal_punctuation(text):
        return text
    right_stripped = text.rstrip()
    return f"{right_stripped}{qwen_sentence_terminator(language, text)}{text[len(right_stripped):]}"


def has_terminal_punctuation(text: str) -> bool:
    stripped = normalize_transcribed_text(text)
    while stripped and stripped[-1] in QWEN_TRAILING_CLOSERS:
        stripped = stripped[:-1].rstrip()
    return bool(stripped and stripped[-1] in QWEN_TERMINAL_PUNCTUATION)


def qwen_sentence_terminator(language: str | None, text: str) -> str:
    language_key = str(language or "").strip().lower()
    if language_key in QWEN_CJK_SENTENCE_LANGUAGES or contains_cjk_sentence_script(text):
        return "。"
    return "."


def contains_cjk_sentence_script(text: str) -> bool:
    for character in text:
        codepoint = ord(character)
        if 0x3040 <= codepoint <= 0x30FF or 0x3400 <= codepoint <= 0x9FFF or 0xF900 <= codepoint <= 0xFAFF:
            return True
    return False


def restore_qwen_timestamp_spacing(parts: list[TimedTextPart], source_text: str) -> list[TimedTextPart]:
    if not source_text or not parts:
        return parts

    spaced: list[TimedTextPart] = []
    cursor = 0
    for part in parts:
        token = part.text.strip()
        text = part.text
        index = source_text.find(token, cursor) if token else -1
        if index >= 0:
            prefix = source_text[cursor:index]
            if spaced and any(ch.isspace() for ch in prefix):
                text = " " + text.lstrip()
            cursor = index + len(token)

        spaced.append(TimedTextPart(text, part.start_ms, part.end_ms, part.has_word_timing))

    return spaced


def timed_text_parts_from_timestamp_items(items: Any) -> list[TimedTextPart]:
    if not items:
        return []

    parts: list[TimedTextPart] = []
    for item in items:
        text = ""
        start = None
        end = None
        if isinstance(item, dict):
            text = str(item.get("text") or item.get("word") or "")
            start = item.get("start")
            if start is None:
                start = item.get("start_time")
            if start is None:
                start = item.get("begin")
            end = item.get("end")
            if end is None:
                end = item.get("end_time")
        elif isinstance(item, (list, tuple)) and len(item) >= 3:
            start, end, text = item[0], item[1], str(item[2])
        else:
            text = str(getattr(item, "text", getattr(item, "word", "")) or "")
            start = getattr(item, "start", None)
            if start is None:
                start = getattr(item, "start_time", None)
            end = getattr(item, "end", None)
            if end is None:
                end = getattr(item, "end_time", None)

        try:
            start_ms = int(float(start) * 1000)
            end_ms = int(float(end) * 1000)
        except (TypeError, ValueError):
            continue
        text = normalize_transcribed_text(text)
        if text:
            parts.append(TimedTextPart(text, max(0, start_ms), max(start_ms + 1, end_ms), True))
    return parts


def load_whisperx_model(model_name: str, language: str | None, device: str, compute_type: str, models_dir: Path) -> dict[str, Any]:
    suppress_torchcodec_warning()
    apply_torchaudio_compatibility_shims()
    patch_speechbrain_lazy_module_inspection()

    import whisperx

    models_dir.mkdir(parents=True, exist_ok=True)
    asr_options = {
        "beam_size": whisper_transcribe_options_for_whisperx(model_name, compute_type).get("beam_size", 1),
        "condition_on_previous_text": False,
        "compression_ratio_threshold": WHISPER_COMPRESSION_RATIO_THRESHOLD,
        "log_prob_threshold": WHISPER_LOG_PROB_THRESHOLD,
        "no_speech_threshold": WHISPER_NO_SPEECH_THRESHOLD,
    }
    kwargs = {
        "compute_type": compute_type,
        "language": language,
        "download_root": str(models_dir),
        "asr_options": asr_options,
        "vad_options": {"vad_onset": 0.5, "vad_offset": 0.363},
    }
    last_error: Exception | None = None
    for candidate in (
        kwargs,
        {key: value for key, value in kwargs.items() if key != "vad_options"},
        {key: value for key, value in kwargs.items() if key not in {"vad_options", "asr_options"}},
        {"compute_type": compute_type, "download_root": str(models_dir)},
    ):
        try:
            model = whisperx.load_model(model_name, device, **candidate)
            return {
                "module": whisperx,
                "model": model,
                "device": device,
                "language": language,
                "models_dir": models_dir,
            }
        except TypeError as exc:
            last_error = exc

    raise RuntimeError(f"WhisperX model could not be loaded: {last_error}")


def whisper_transcribe_options_for_whisperx(_model_name: str, _compute_type: str) -> dict[str, Any]:
    return whisper_transcribe_options(100)


def transcribe_with_whisperx(engine: dict[str, Any], wav_path: Path, languages: list[str], quality_preset: int) -> list[TimedTextPart]:
    whisperx = engine["module"]
    model = engine["model"]
    device = str(engine.get("device") or "cpu")
    models_dir = Path(engine.get("models_dir") or wav_path.parent)
    language = languages[0] if len(languages) == 1 else engine.get("language")
    batch_size = int(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WHISPERX_BATCH_SIZE", "8" if device == "cuda" else "1"))
    chunk_size = transcribe_chunk_seconds_for_quality(quality_preset)
    audio = whisperx.load_audio(str(wav_path)) if hasattr(whisperx, "load_audio") else str(wav_path)

    result = whisperx_model_transcribe(model, audio, batch_size, chunk_size)
    if isinstance(result, dict) and language and not result.get("language"):
        result["language"] = language
    result = maybe_align_whisperx_result(whisperx, result, audio, device, models_dir)
    return timed_text_parts_for_whisperx_result(result, wav_duration_ms(wav_path))


def whisperx_model_transcribe(model: Any, audio: Any, batch_size: int, chunk_size: int) -> Any:
    for kwargs in (
        {"batch_size": batch_size, "chunk_size": chunk_size},
        {"batch_size": batch_size},
        {},
    ):
        try:
            return model.transcribe(audio, **kwargs)
        except TypeError:
            continue
    return model.transcribe(audio)


def maybe_align_whisperx_result(whisperx: Any, result: Any, audio: Any, device: str, models_dir: Path) -> Any:
    if os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WHISPERX_ALIGN", "true").lower() not in {"1", "true", "yes", "on"}:
        return result
    if not isinstance(result, dict):
        return result
    segments = result.get("segments") or []
    if not segments:
        return result
    language = str(result.get("language") or "").lower()
    if not language:
        return result

    try:
        align_model, metadata = load_whisperx_align_model(whisperx, language, device, models_dir)
        return whisperx.align(
            segments,
            align_model,
            metadata,
            audio,
            device,
            return_char_alignments=False,
            print_progress=False,
        )
    except TypeError:
        try:
            align_model, metadata = load_whisperx_align_model(whisperx, language, device, models_dir)
            return whisperx.align(segments, align_model, metadata, audio, device)
        except Exception:
            return result
    except Exception:
        return result


def load_whisperx_align_model(whisperx: Any, language: str, device: str, models_dir: Path) -> tuple[Any, Any]:
    align_dir = models_dir / "align"
    align_dir.mkdir(parents=True, exist_ok=True)
    for kwargs in (
        {"model_dir": str(align_dir), "model_cache_only": False},
        {"model_dir": str(align_dir)},
        {},
    ):
        try:
            return whisperx.load_align_model(language_code=language, device=device, **kwargs)
        except TypeError:
            continue
    return whisperx.load_align_model(language_code=language, device=device)


def timed_text_parts_for_whisperx_result(result: Any, fallback_duration_ms: int) -> list[TimedTextPart]:
    segments = value_from_object(result, "segments") if result is not None else None
    if not segments:
        text = filter_transcribed_caption_text(str(value_from_object(result, "text") or ""))
        return [TimedTextPart(text, 0, max(1, fallback_duration_ms), False)] if text else []

    parts: list[TimedTextPart] = []
    for segment in segments:
        segment_text = str(value_from_object(segment, "text") or "")
        if segment_text and not filter_transcribed_caption_text(segment_text):
            continue

        word_parts = timed_text_parts_for_whisperx_words(value_from_object(segment, "words"))
        if word_parts:
            parts.extend(word_parts)
            continue

        text = filter_transcribed_caption_text(segment_text)
        if not text:
            continue
        start_seconds = optional_float(value_from_object(segment, "start")) or 0.0
        end_seconds = optional_float(value_from_object(segment, "end"))
        start_ms = max(0, int(start_seconds * 1000))
        end_ms = int(end_seconds * 1000) if end_seconds is not None and end_seconds > start_seconds else start_ms
        parts.append(TimedTextPart(text, start_ms, max(start_ms + 1, end_ms), False))

    return parts


def timed_text_parts_for_whisperx_words(words: Any) -> list[TimedTextPart]:
    if not words:
        return []

    parts: list[TimedTextPart] = []
    for word in words:
        text = str(value_from_object(word, "word") or value_from_object(word, "text") or "")
        if not text.strip():
            continue
        start_seconds = optional_float(value_from_object(word, "start"))
        end_seconds = optional_float(value_from_object(word, "end"))
        if start_seconds is None or end_seconds is None or end_seconds <= start_seconds:
            return []
        parts.append(TimedTextPart(
            text,
            max(0, int(start_seconds * 1000)),
            max(1, int(end_seconds * 1000)),
            True,
        ))
    return parts


def load_whisperlivekit_engine(model_name: str, language: str | None, device: str, max_speakers: int, diarization: bool = True) -> Any:
    import whisperlivekit

    TranscriptionEngine = getattr(whisperlivekit, "TranscriptionEngine")
    WhisperLiveKitConfig = getattr(whisperlivekit, "WhisperLiveKitConfig", None)

    backend = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_BACKEND", "faster-whisper")
    backend_policy = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_BACKEND_POLICY", "simulstreaming")
    diarization_backend = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DIARIZATION_BACKEND", "sortformer")
    max_context_tokens = int(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_MAX_CONTEXT_TOKENS", str(WHISPERLIVEKIT_MAX_CONTEXT_TOKENS)))
    normalized_model_name = str(model_name or "").strip().lower()
    model_size = model_name if not normalized_model_name.startswith("qwen3-asr") else "large-v3-turbo"
    if normalized_model_name in {"", "default", "whisperlivekit-default"}:
        model_size = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DEFAULT_MODEL", "large-v3-turbo")
    kwargs = dict(
        model_size=model_size,
        lan=language or "auto",
        backend=backend,
        backend_policy=backend_policy,
        diarization=diarization,
        diarization_backend=diarization_backend,
        max_context_tokens=max(0, max_context_tokens),
        pcm_input=True,
    )
    last_error: Exception | None = None
    try:
        return TranscriptionEngine(**kwargs)
    except TypeError as exc:
        last_error = exc
        pass

    if WhisperLiveKitConfig is not None:
        from_kwargs = getattr(WhisperLiveKitConfig, "from_kwargs", None)
        config = from_kwargs(**kwargs) if callable(from_kwargs) else WhisperLiveKitConfig(**kwargs)
        return TranscriptionEngine(config=config)

    raise RuntimeError(f"WhisperLiveKit TranscriptionEngine could not be created: {last_error}")


class WhisperLiveKitStreamingSession:
    def __init__(self, engine: Any) -> None:
        self.engine = engine
        self.loop = asyncio.new_event_loop()
        self.ready = threading.Event()
        self.thread = threading.Thread(target=self._run_loop, name="live-dialogue-translator-wlk", daemon=True)
        self.processor = None
        self.collect_task = None
        self.results: list[Any] = []
        self.results_lock = threading.RLock()
        self.transcribe_lock = threading.RLock()
        self.emitted_keys: set[tuple[str, str, int, int]] = set()
        self.emitted_line_refs: list[tuple[str, int, int]] = []
        self.emitted_audio_watermark_ms = -1
        self.processed_result_count = 0
        self.session_start_ms: int | None = None
        self.closed = False
        self.thread.start()
        self.ready.wait(timeout=float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_START_TIMEOUT_SECONDS", "10")))
        if not self.ready.is_set():
            raise TimeoutError("WhisperLiveKit streaming session did not start")
        self._wait_for(self._start(), "start")

    def _run_loop(self) -> None:
        asyncio.set_event_loop(self.loop)
        self.ready.set()
        self.loop.run_forever()

    async def _start(self) -> None:
        from whisperlivekit.audio_processor import AudioProcessor

        self.processor = AudioProcessor(transcription_engine=self.engine)
        results_gen = self.processor.create_tasks()
        if asyncio.iscoroutine(results_gen):
            results_gen = await results_gen
        self.collect_task = asyncio.create_task(self._collect_results(results_gen))

    async def _collect_results(self, results_gen: Any) -> None:
        async for front_data in results_gen:
            result = front_data.to_dict() if hasattr(front_data, "to_dict") else front_data
            with self.results_lock:
                self.results.append(result)

    def transcribe_wav(self, wav_path: Path, base_start_ms: int, latency_ms: int) -> list[dict[str, Any]]:
        return self.transcribe_pcm(read_wav_pcm(wav_path), base_start_ms, latency_ms)

    def transcribe_pcm(self, pcm: bytes, base_start_ms: int, latency_ms: int) -> list[dict[str, Any]]:
        with self.transcribe_lock:
            if self.closed:
                raise RuntimeError("WhisperLiveKit streaming session is closed")
            if self.session_start_ms is None:
                self.session_start_ms = base_start_ms
            self._wait_for(self._feed_pcm(pcm), "feed audio")
            drain_seconds = float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_DRAIN_SECONDS", "0.25"))
            if drain_seconds > 0:
                time.sleep(drain_seconds)
            self._raise_collect_exception_if_done()
            return self._new_events(latency_ms, base_start_ms)

    async def _feed_pcm(self, pcm: bytes) -> None:
        if self.processor is None:
            raise RuntimeError("WhisperLiveKit streaming session is not initialized")
        chunk_bytes = int(float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS", "0.5")) * BYTES_PER_SECOND)
        chunk_bytes = max(2, (chunk_bytes // 2) * 2)
        for offset in range(0, len(pcm), chunk_bytes):
            await self.processor.process_audio(pcm[offset:offset + chunk_bytes])

    def _new_events(self, latency_ms: int, current_audio_start_ms: int) -> list[dict[str, Any]]:
        base_start_ms = self.session_start_ms or 0
        with self.results_lock:
            snapshot = list(self.results)

        if not snapshot:
            return []

        new_results = snapshot[self.processed_result_count:]
        self.processed_result_count = len(snapshot)
        candidate_events: list[dict[str, Any]] = []
        for result in new_results:
            candidate_events.extend(worker_events_for_whisperlivekit_result(result, base_start_ms, latency_ms))

        events: list[dict[str, Any]] = []
        for event in sorted(candidate_events, key=lambda item: (
            int(item.get("startMs", 0)),
            max(0, int(item.get("endMs", 0)) - int(item.get("startMs", 0))),
            len(str(item.get("text", ""))),
        )):
            key = (
                str(event.get("speakerId", "")),
                str(event.get("text", "")),
                int(event.get("startMs", 0)),
                int(event.get("endMs", 0)),
            )
            if key in self.emitted_keys:
                continue
            if self._is_stale_backfill_event(event, current_audio_start_ms):
                continue
            if self._is_aggregate_duplicate_event(event, events):
                continue
            if self._is_revision_duplicate_event(event, events):
                continue
            if self.emitted_audio_watermark_ms >= 0 and int(event.get("endMs", 0)) <= self.emitted_audio_watermark_ms - 500:
                continue
            self.emitted_keys.add(key)
            self.emitted_line_refs.append((
                compact_caption_text(str(event.get("text", ""))),
                int(event.get("startMs", 0)),
                int(event.get("endMs", 0)),
            ))
            if len(self.emitted_line_refs) > WHISPERLIVEKIT_EMITTED_REF_LIMIT:
                del self.emitted_line_refs[:-WHISPERLIVEKIT_EMITTED_REF_LIMIT]
            self.emitted_audio_watermark_ms = max(self.emitted_audio_watermark_ms, int(event.get("endMs", 0)))
            events.append(event)
        return sorted(events, key=lambda item: (int(item.get("startMs", 0)), int(item.get("endMs", 0))))

    def _is_aggregate_duplicate_event(self, event: dict[str, Any], current_events: list[dict[str, Any]]) -> bool:
        text = compact_caption_text(str(event.get("text", "")))
        if not text:
            return True
        start_ms = int(event.get("startMs", 0))
        end_ms = int(event.get("endMs", 0))
        refs = list(self.emitted_line_refs)
        refs.extend(
            (
                compact_caption_text(str(item.get("text", ""))),
                int(item.get("startMs", 0)),
                int(item.get("endMs", 0)),
            )
            for item in current_events
        )
        contained_refs = [
            (ref_text, ref_start_ms, ref_end_ms)
            for ref_text, ref_start_ms, ref_end_ms in refs
            if ref_text
            and len(text) > len(ref_text)
            and ref_text in text
            and start_ms <= ref_start_ms + 150
            and end_ms >= ref_end_ms - 150
        ]
        if len(contained_refs) >= 2:
            return True
        if len(contained_refs) == 1:
            ref_text, ref_start_ms, ref_end_ms = contained_refs[0]
            return (
                text.startswith(ref_text)
                and abs(start_ms - ref_start_ms) <= 250
                and end_ms >= ref_end_ms
        )
        return False

    def _is_stale_backfill_event(self, event: dict[str, Any], current_audio_start_ms: int) -> bool:
        if self.emitted_audio_watermark_ms < 0:
            return False
        max_retrospective_ms = int(os.environ.get(
            "LIVE_DIALOGUE_TRANSLATOR_WLK_MAX_RETROSPECTIVE_MS",
            str(WHISPERLIVEKIT_MAX_RETROSPECTIVE_MS),
        ))
        return int(event.get("endMs", 0)) < current_audio_start_ms - max(0, max_retrospective_ms)

    def _is_revision_duplicate_event(self, event: dict[str, Any], current_events: list[dict[str, Any]]) -> bool:
        text = compact_caption_text(str(event.get("text", "")))
        if len(text) < WHISPERLIVEKIT_REVISION_DUPLICATE_MIN_CHARS:
            return False
        start_ms = int(event.get("startMs", 0))
        end_ms = int(event.get("endMs", 0))
        refs = list(self.emitted_line_refs)
        refs.extend(
            (
                compact_caption_text(str(item.get("text", ""))),
                int(item.get("startMs", 0)),
                int(item.get("endMs", 0)),
            )
            for item in current_events
        )
        return any(
            self._is_revision_duplicate_text(text, ref_text)
            and self._caption_ranges_overlap_or_close(start_ms, end_ms, ref_start_ms, ref_end_ms)
            for ref_text, ref_start_ms, ref_end_ms in refs
        )

    @staticmethod
    def _is_revision_duplicate_text(text: str, ref_text: str) -> bool:
        if text == ref_text:
            return True
        if (
            len(text) < WHISPERLIVEKIT_REVISION_DUPLICATE_MIN_CHARS
            or len(ref_text) < WHISPERLIVEKIT_REVISION_DUPLICATE_MIN_CHARS
        ):
            return False
        shorter, longer = sorted((text, ref_text), key=len)
        return shorter in longer and len(shorter) / max(1, len(longer)) >= 0.85

    @staticmethod
    def _caption_ranges_overlap_or_close(start_ms: int, end_ms: int, ref_start_ms: int, ref_end_ms: int) -> bool:
        if end_ms <= start_ms:
            end_ms = start_ms + 1
        if ref_end_ms <= ref_start_ms:
            ref_end_ms = ref_start_ms + 1
        overlap_ms = min(end_ms, ref_end_ms) - max(start_ms, ref_start_ms)
        if overlap_ms > 0:
            min_duration_ms = max(1, min(end_ms - start_ms, ref_end_ms - ref_start_ms))
            if overlap_ms / min_duration_ms >= WHISPERLIVEKIT_REVISION_DUPLICATE_OVERLAP_RATIO:
                return True
        return (
            abs(start_ms - ref_start_ms) <= WHISPERLIVEKIT_REVISION_DUPLICATE_TIME_TOLERANCE_MS
            and abs(end_ms - ref_end_ms) <= WHISPERLIVEKIT_REVISION_DUPLICATE_TIME_TOLERANCE_MS
        )

    def _raise_collect_exception_if_done(self) -> None:
        task = self.collect_task
        if task is not None and task.done() and not task.cancelled():
            exc = task.exception()
            if exc is not None:
                raise exc

    def close(self) -> None:
        with self.transcribe_lock:
            if self.closed:
                return
            self.closed = True
            try:
                self._wait_for(self._stop(), "stop", timeout=float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_STOP_TIMEOUT_SECONDS", "5")))
            finally:
                if self.loop.is_running():
                    self.loop.call_soon_threadsafe(self.loop.stop)
                self.thread.join(timeout=2)
                with contextlib.suppress(Exception):
                    self.loop.close()

    async def _stop(self) -> None:
        if self.processor is not None:
            with contextlib.suppress(Exception):
                await self.processor.process_audio(b"")
        task = self.collect_task
        if task is not None:
            try:
                await asyncio.wait_for(task, timeout=float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_COLLECT_STOP_TIMEOUT_SECONDS", "2")))
            except asyncio.TimeoutError:
                task.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await task
        if self.processor is not None:
            cleanup = getattr(self.processor, "cleanup", None)
            if callable(cleanup):
                cleanup_result = cleanup()
                if asyncio.iscoroutine(cleanup_result):
                    await cleanup_result

    def _wait_for(self, coro: Any, action: str, timeout: float | None = None) -> Any:
        future = asyncio.run_coroutine_threadsafe(coro, self.loop)
        wait_timeout = timeout if timeout is not None else float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_TIMEOUT_SECONDS", "120"))
        try:
            return future.result(timeout=wait_timeout)
        except TimeoutError:
            future.cancel()
            raise TimeoutError(f"WhisperLiveKit streaming session did not {action} within {wait_timeout:g}s")


def transcribe_with_whisperlivekit(engine: Any, wav_path: Path, base_start_ms: int, latency_ms: int) -> list[dict[str, Any]]:
    result = asyncio.run(transcribe_with_whisperlivekit_in_process(engine, wav_path))
    return worker_events_for_whisperlivekit_result(result, base_start_ms, latency_ms)


async def transcribe_with_whisperlivekit_in_process(engine: Any, wav_path: Path) -> Any:
    from whisperlivekit.audio_processor import AudioProcessor

    pcm = read_wav_pcm(wav_path)
    processor = AudioProcessor(transcription_engine=engine)
    results_gen = processor.create_tasks()
    if asyncio.iscoroutine(results_gen):
        results_gen = await results_gen

    latest_result: Any = None

    async def collect_results() -> None:
        nonlocal latest_result
        async for front_data in results_gen:
            latest_result = front_data.to_dict() if hasattr(front_data, "to_dict") else front_data

    collect_task = asyncio.create_task(collect_results())
    timeout = float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_TIMEOUT_SECONDS", "120"))
    chunk_bytes = int(float(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_WLK_CHUNK_SECONDS", "0.5")) * BYTES_PER_SECOND)
    chunk_bytes = max(2, (chunk_bytes // 2) * 2)

    try:
        for offset in range(0, len(pcm), chunk_bytes):
            await processor.process_audio(pcm[offset:offset + chunk_bytes])
        await processor.process_audio(b"")
        try:
            await asyncio.wait_for(collect_task, timeout=timeout)
        except asyncio.TimeoutError:
            collect_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await collect_task
            if latest_result is None:
                raise TimeoutError(f"WhisperLiveKit did not finish within {timeout:g}s")
    finally:
        cleanup = getattr(processor, "cleanup", None)
        if callable(cleanup):
            cleanup_result = cleanup()
            if asyncio.iscoroutine(cleanup_result):
                await cleanup_result

    return latest_result


def read_wav_pcm(wav_path: Path) -> bytes:
    with wave.open(str(wav_path), "rb") as wav:
        channels = wav.getnchannels()
        sample_width = wav.getsampwidth()
        frame_rate = wav.getframerate()
        if channels != 1 or sample_width != 2 or frame_rate != SAMPLE_RATE:
            raise ValueError(f"WhisperLiveKit expects {SAMPLE_RATE}Hz mono 16-bit PCM WAV")
        return wav.readframes(wav.getnframes())


def worker_events_for_whisperlivekit_result(result: Any, base_start_ms: int, latency_ms: int) -> list[dict[str, Any]]:
    if result is None:
        return []

    lines = getattr(result, "lines", None) or getattr(result, "segments", None)
    if isinstance(result, dict):
        lines = result.get("lines") or result.get("segments")
    events: list[dict[str, Any]] = []
    if lines:
        for line in lines:
            text = value_from_object(line, "text")
            if text is None:
                continue
            text = filter_transcribed_caption_text(str(text))
            if not text:
                continue
            start_ms = base_start_ms + parse_time_ms(value_from_object(line, "start"), 0)
            end_ms = base_start_ms + parse_time_ms(value_from_object(line, "end"), start_ms - base_start_ms + 1)
            speaker = value_from_object(line, "speaker")
            event = {
                "type": "final_caption",
                "speakerId": normalize_external_speaker_id(speaker),
                "text": text,
                "startMs": start_ms,
                "endMs": max(start_ms + 1, end_ms),
                "latencyMs": latency_ms,
            }
            events.extend(split_whisperlivekit_caption_event(event))
        return events

    text = filter_transcribed_caption_text(str(getattr(result, "text", "") if not isinstance(result, dict) else result.get("text", "")))
    if not text:
        return []
    return split_whisperlivekit_caption_event({
        "type": "final_caption",
        "speakerId": "speaker_1",
        "text": text,
        "startMs": base_start_ms,
        "endMs": base_start_ms + 1,
        "latencyMs": latency_ms,
    })


def value_from_object(value: Any, name: str) -> Any:
    return value.get(name) if isinstance(value, dict) else getattr(value, name, None)


def parse_time_ms(value: Any, default_ms: int) -> int:
    if value is None:
        return default_ms
    if isinstance(value, str) and ":" in value:
        parts = [float(part) for part in value.split(":")]
        seconds = 0.0
        for part in parts:
            seconds = seconds * 60 + part
        return int(seconds * 1000)
    try:
        numeric = float(value)
    except (TypeError, ValueError):
        return default_ms
    return int(numeric * 1000 if numeric < 100000 else numeric)


def split_whisperlivekit_caption_event(event: dict[str, Any]) -> list[dict[str, Any]]:
    text = str(event.get("text", "")).strip()
    if not text:
        return []

    start_ms = int(event.get("startMs", 0))
    end_ms = max(start_ms + 1, int(event.get("endMs", start_ms + 1)))
    chunks = whisperlivekit_caption_chunks(text, end_ms - start_ms)
    if len(chunks) <= 1:
        return [event]

    weights = [max(1, len(compact_caption_text(chunk))) for chunk in chunks]
    total_weight = max(1, sum(weights))
    duration = max(len(chunks), end_ms - start_ms)
    split_events: list[dict[str, Any]] = []
    elapsed_weight = 0
    current_start_ms = start_ms
    for index, (chunk, weight) in enumerate(zip(chunks, weights)):
        elapsed_weight += weight
        if index == len(chunks) - 1:
            current_end_ms = end_ms
        else:
            current_end_ms = start_ms + int(duration * elapsed_weight / total_weight)
            current_end_ms = max(current_start_ms + 1, min(end_ms - (len(chunks) - index - 1), current_end_ms))
        split_event = dict(event)
        split_event["text"] = chunk
        split_event["startMs"] = current_start_ms
        split_event["endMs"] = max(current_start_ms + 1, current_end_ms)
        split_events.append(split_event)
        current_start_ms = split_event["endMs"]
    return split_events


def whisperlivekit_caption_chunks(text: str, duration_ms: int) -> list[str]:
    text = " ".join(text.split())
    if len(text) <= WHISPERLIVEKIT_MAX_CAPTION_CHARS and duration_ms <= WHISPERLIVEKIT_MAX_CAPTION_MS:
        return [text]

    sentences = split_caption_sentences(text)
    if len(sentences) > 1 and duration_ms > WHISPERLIVEKIT_MAX_CAPTION_MS:
        chunks: list[str] = []
        for sentence in sentences:
            chunks.extend(split_caption_by_length(sentence, WHISPERLIVEKIT_MAX_CAPTION_CHARS))
        return chunks

    return split_caption_by_length(text, WHISPERLIVEKIT_MAX_CAPTION_CHARS)


def split_caption_sentences(text: str) -> list[str]:
    sentences: list[str] = []
    current: list[str] = []
    for char in text:
        current.append(char)
        if char in ".!?。！？":
            sentence = "".join(current).strip()
            if sentence:
                sentences.append(sentence)
            current = []
    remainder = "".join(current).strip()
    if remainder:
        sentences.append(remainder)
    return sentences or [text]


def split_caption_by_length(text: str, max_chars: int) -> list[str]:
    if len(text) <= max_chars:
        return [text]

    words = text.split()
    if len(words) <= 1:
        return [text[index:index + max_chars].strip() for index in range(0, len(text), max_chars) if text[index:index + max_chars].strip()]

    chunks: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if current and len(candidate) > max_chars:
            chunks.append(current)
            current = word
        else:
            current = candidate
    if current:
        chunks.append(current)
    return chunks


def normalize_external_speaker_id(value: Any) -> str:
    if value is None:
        return "speaker_1"
    text = str(value).strip().lower()
    if text.startswith("speaker_"):
        return text
    try:
        number = int(float(text))
        return f"speaker_{max(1, number)}"
    except ValueError:
        return "speaker_" + "".join(ch if ch.isalnum() else "_" for ch in text).strip("_")


def compact_caption_text(text: str) -> str:
    return "".join(str(text).split())


def wav_duration_ms(wav_path: Path) -> int:
    with wave.open(str(wav_path), "rb") as wav:
        return int(wav.getnframes() / max(1, wav.getframerate()) * 1000)


def write_wav(path: Path, pcm: bytes) -> None:
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(pcm)


def select_current_speaker_label(diarization: Any, context_seconds: float, current_seconds: float) -> str | None:
    diarization = unwrap_diarization_annotation(diarization)
    decision_seconds = min(context_seconds, max(current_seconds, DIARIZATION_DECISION_SECONDS))
    tail_start = max(0.0, context_seconds - decision_seconds)
    scores: dict[str, float] = {}
    itertracks = getattr(diarization, "itertracks", None)
    if callable(itertracks):
        try:
            for segment, _track, label in itertracks(yield_label=True):
                start = float(getattr(segment, "start", 0.0))
                end = float(getattr(segment, "end", start))
                overlap = max(0.0, min(end, context_seconds) - max(start, tail_start))
                if overlap > 0:
                    scores[str(label)] = scores.get(str(label), 0.0) + overlap
        except TypeError:
            scores.clear()

    if scores:
        label, overlap = max(scores.items(), key=lambda item: item[1])
        total_overlap = sum(scores.values())
        if overlap < DIARIZATION_MIN_LABEL_SECONDS:
            return None
        if total_overlap > 0 and overlap / total_overlap < DIARIZATION_MIN_LABEL_RATIO:
            return None
        return label

    labels_fn = getattr(diarization, "labels", None)
    if not callable(labels_fn):
        return None

    labels = list(labels_fn())
    return str(labels[-1]) if labels else None


def diarization_turns_for_window(
    diarization: Any,
    window_start_seconds: float,
    window_end_seconds: float,
) -> list[tuple[str, float, float]]:
    annotation = unwrap_diarization_annotation(diarization)
    itertracks = getattr(annotation, "itertracks", None)
    if not callable(itertracks):
        return []

    turns: list[tuple[str, float, float]] = []
    try:
        for segment, _track, label in itertracks(yield_label=True):
            start = max(window_start_seconds, float(getattr(segment, "start", 0.0)))
            end = min(window_end_seconds, float(getattr(segment, "end", start)))
            if end - start < 0.5:
                continue
            turns.append((str(label), start, end))
    except TypeError:
        return []

    turns.sort(key=lambda item: (item[1], item[2], item[0]))
    merged: list[tuple[str, float, float]] = []
    for label, start, end in turns:
        if merged and merged[-1][0] == label and start - merged[-1][2] <= 0.25:
            previous = merged[-1]
            merged[-1] = (previous[0], previous[1], max(previous[2], end))
        else:
            merged.append((label, start, end))
    return merged


def diarization_turn_confidence(
    start_seconds: float,
    end_seconds: float,
    context_seconds: float | None = None,
) -> float:
    duration_seconds = max(0.0, end_seconds - start_seconds)
    if duration_seconds <= 0:
        return 0.0

    duration_score = min(1.0, duration_seconds / 2.0)
    if context_seconds is None:
        context_score = 1.0
    else:
        context_score = min(1.0, max(0.25, context_seconds / DIARIZATION_MIN_CONTEXT_SECONDS))

    return round(max(0.2, min(0.95, 0.25 + (0.7 * duration_score * context_score))), 3)


def exclusive_diarization_turns(
    turns: list[tuple[str, float, float]],
    preferred_label: str | None = None,
) -> list[tuple[str, float, float]]:
    if not turns:
        return []

    result: list[tuple[str, float, float]] = []
    index = 0
    while index < len(turns):
        label, start, end = turns[index]
        group = [(label, start, end)]
        index += 1
        while index < len(turns):
            next_label, next_start, next_end = turns[index]
            if abs(next_start - start) > 0.05 or abs(next_end - end) > 0.05:
                break
            group.append((next_label, next_start, next_end))
            index += 1

        if len(group) == 1:
            result.append(group[0])
            continue

        chosen = next((candidate for candidate in group if candidate[0] == preferred_label), None)
        result.append(chosen or group[0])

    return result


def speaker_handoff_adjusted_turns(turns: list[tuple[str, float, float]]) -> list[tuple[str, float, float]]:
    if len(turns) < 2:
        return turns

    adjusted = [[label, start, end] for label, start, end in turns]
    for index in range(1, len(adjusted)):
        previous = adjusted[index - 1]
        current = adjusted[index]
        previous_label = str(previous[0])
        current_label = str(current[0])
        if previous_label == current_label:
            continue

        previous_start = float(previous[1])
        previous_end = float(previous[2])
        current_start = float(current[1])
        current_end = float(current[2])
        if current_start - previous_end > DIARIZATION_HANDOFF_MAX_GAP_SECONDS:
            continue

        handoff_start = current_start - DIARIZATION_HANDOFF_PREROLL_SECONDS
        handoff_start = max(handoff_start, previous_start + DIARIZATION_MIN_TURN_SLICE_SECONDS)
        handoff_start = min(handoff_start, current_end - DIARIZATION_MIN_TURN_SLICE_SECONDS)
        if handoff_start <= previous_start or handoff_start >= current_end:
            continue

        current[1] = min(current_start, handoff_start)
        if previous_end > handoff_start:
            previous[2] = handoff_start

    return [
        (str(label), float(start), float(end))
        for label, start, end in adjusted
        if float(end) - float(start) >= DIARIZATION_MIN_TURN_SLICE_SECONDS
    ]


def speaker_for_time_range(speaker_turns: list[tuple[int, int, str]], start_ms: int, end_ms: int) -> str | None:
    midpoint = start_ms + max(0, end_ms - start_ms) / 2
    for turn_start_ms, turn_end_ms, speaker_id in speaker_turns:
        if turn_start_ms <= midpoint <= turn_end_ms:
            return speaker_id

    best_speaker_id = None
    best_overlap = 0
    for turn_start_ms, turn_end_ms, speaker_id in speaker_turns:
        overlap = min(end_ms, turn_end_ms) - max(start_ms, turn_start_ms)
        if overlap > best_overlap:
            best_overlap = overlap
            best_speaker_id = speaker_id

    return best_speaker_id


def unwrap_diarization_annotation(diarization: Any) -> Any:
    exclusive = getattr(diarization, "exclusive_speaker_diarization", None)
    if exclusive is not None:
        return exclusive

    speaker_diarization = getattr(diarization, "speaker_diarization", None)
    if speaker_diarization is not None:
        return speaker_diarization

    return diarization


def speaker_embedding_for_label(diarization: Any, label: str) -> Any | None:
    embeddings = getattr(diarization, "speaker_embeddings", None)
    if embeddings is None:
        return None

    annotation = getattr(diarization, "speaker_diarization", None)
    if annotation is None:
        annotation = unwrap_diarization_annotation(diarization)

    labels_fn = getattr(annotation, "labels", None)
    if not callable(labels_fn):
        return None

    labels = [str(candidate) for candidate in labels_fn()]
    try:
        index = labels.index(str(label))
    except ValueError:
        return None

    try:
        return embeddings[index]
    except (IndexError, TypeError):
        return None


def transcribe_chunk_seconds_for_quality(quality_preset: int) -> int:
    quality = clamp_quality_preset(quality_preset)
    if quality >= 75:
        return 5
    if quality >= 35:
        return 4
    return 2


def diarization_context_seconds_for_quality(quality_preset: int) -> int:
    quality = clamp_quality_preset(quality_preset)
    if quality >= 75:
        return 120
    if quality >= 35:
        return 60
    return DIARIZATION_CONTEXT_SECONDS


def diart_latency_seconds_for_quality(quality_preset: int) -> float:
    quality = clamp_quality_preset(quality_preset)
    if quality >= 75:
        return 5.0
    if quality >= 35:
        return 2.0
    return 0.5


def diart_duration_seconds_for_quality(_quality_preset: int) -> float:
    return 5.0


def diart_hyper_parameters_for_quality(quality_preset: int) -> dict[str, float]:
    _quality = clamp_quality_preset(quality_preset)
    return {"tau_active": 0.555, "rho_update": 0.422, "delta_new": 1.517}


def whisper_transcribe_options(quality_preset: int = 50) -> dict[str, Any]:
    quality = clamp_quality_preset(quality_preset)
    if quality >= 75:
        beam_size = 5
        max_new_tokens = 128
        word_timestamps = True
    elif quality >= 35:
        beam_size = 3
        max_new_tokens = WHISPER_MAX_NEW_TOKENS
        word_timestamps = True
    else:
        beam_size = 1
        max_new_tokens = 48
        word_timestamps = False

    return {
        "beam_size": beam_size,
        "temperature": 0.0,
        "condition_on_previous_text": False,
        "vad_filter": True,
        "vad_parameters": {
            "min_silence_duration_ms": WHISPER_VAD_MIN_SILENCE_MS,
        },
        "compression_ratio_threshold": WHISPER_COMPRESSION_RATIO_THRESHOLD,
        "log_prob_threshold": WHISPER_LOG_PROB_THRESHOLD,
        "no_speech_threshold": WHISPER_NO_SPEECH_THRESHOLD,
        "no_repeat_ngram_size": 3,
        "repetition_penalty": 1.05,
        "max_new_tokens": max_new_tokens,
        "word_timestamps": word_timestamps,
    }


def relaxed_whisper_transcribe_options(quality_preset: int = 50) -> dict[str, Any]:
    options = whisper_transcribe_options(quality_preset)
    options["vad_filter"] = False
    options.pop("vad_parameters", None)
    options["no_speech_threshold"] = 1.0
    options["log_prob_threshold"] = -2.0
    options["compression_ratio_threshold"] = max(WHISPER_COMPRESSION_RATIO_THRESHOLD, 3.0)
    return options


def slice_pcm_seconds(pcm: bytes, start_seconds: float, end_seconds: float) -> bytes:
    start = max(0, int(start_seconds * SAMPLE_RATE) * 2)
    end = min(len(pcm), int(end_seconds * SAMPLE_RATE) * 2)
    start -= start % 2
    end -= end % 2
    if end <= start:
        return b""
    return pcm[start:end]


def preprocess_stt_pcm(pcm: bytes) -> SttPreprocessResult | None:
    samples = pcm_to_samples(pcm)
    if not samples:
        return None

    samples = remove_dc_offset(samples)
    start_sample, end_sample = speech_bounds_with_padding(samples)
    if end_sample <= start_sample:
        return None

    trimmed = samples[start_sample:end_sample]
    if len(trimmed) < SAMPLE_RATE * STT_MIN_RETAIN_MS // 1000:
        return None

    if samples_rms(trimmed) < VOICE_RMS_THRESHOLD:
        return None

    normalized = normalize_samples(trimmed)
    return SttPreprocessResult(
        pcm=samples_to_pcm(normalized),
        leading_trim_ms=int(start_sample / SAMPLE_RATE * 1000),
        trailing_trim_ms=int((len(samples) - end_sample) / SAMPLE_RATE * 1000),
    )


def pcm_to_samples(pcm: bytes) -> list[int]:
    usable = len(pcm) - (len(pcm) % 2)
    if usable <= 0:
        return []

    values = array("h")
    values.frombytes(pcm[:usable])
    if sys.byteorder != "little":
        values.byteswap()
    return values.tolist()


def samples_to_pcm(samples: list[int]) -> bytes:
    values = array("h", [clip_int16(sample) for sample in samples])
    if sys.byteorder != "little":
        values.byteswap()
    return values.tobytes()


def remove_dc_offset(samples: list[int]) -> list[int]:
    if not samples:
        return samples

    offset = sum(samples) / len(samples)
    if abs(offset) < 1.0:
        return samples

    return [clip_int16(round(sample - offset)) for sample in samples]


def speech_bounds_with_padding(samples: list[int]) -> tuple[int, int]:
    frame_size = max(1, SAMPLE_RATE * STT_TRIM_FRAME_MS // 1000)
    frame_rms = [
        samples_rms(samples[index:index + frame_size])
        for index in range(0, len(samples), frame_size)
    ]
    if not frame_rms:
        return 0, 0

    sorted_rms = sorted(frame_rms)
    noise_index = max(0, min(len(sorted_rms) - 1, int(len(sorted_rms) * 0.2)))
    noise_floor = sorted_rms[noise_index]
    threshold = max(VOICE_RMS_THRESHOLD, min(450.0, noise_floor * 2.0))
    active = [index for index, value in enumerate(frame_rms) if value >= threshold]
    if not active:
        return 0, 0

    padding = SAMPLE_RATE * STT_TRIM_PADDING_MS // 1000
    start = max(0, active[0] * frame_size - padding)
    end = min(len(samples), (active[-1] + 1) * frame_size + padding)
    return start, end


def normalize_samples(samples: list[int]) -> list[int]:
    rms = samples_rms(samples)
    if rms <= 1.0:
        return samples

    peak = max(abs(sample) for sample in samples) or 1
    gain = min(STT_MAX_GAIN, STT_TARGET_RMS / rms)
    if peak * gain > STT_PEAK_CEILING:
        gain = STT_PEAK_CEILING / peak

    if abs(gain - 1.0) < 0.05:
        return samples

    return [clip_int16(round(sample * gain)) for sample in samples]


def samples_rms(samples: list[int]) -> float:
    if not samples:
        return 0.0

    return math.sqrt(sum(sample * sample for sample in samples) / len(samples))


def clip_int16(value: float | int) -> int:
    return max(-32768, min(32767, int(value)))


def join_segments_text(segments: Any) -> str:
    return normalize_transcribed_text(" ".join(
        filter_transcribed_caption_text(segment.text)
        for segment in segments
        if filter_transcribed_caption_text(segment.text) and not is_probable_whisper_hallucination(segment)
    ))


def timed_text_parts_for_segments(segments: list[Any]) -> list[TimedTextPart]:
    parts: list[TimedTextPart] = []
    for segment in segments:
        if is_probable_whisper_hallucination(segment):
            continue

        word_parts = timed_text_parts_for_words(getattr(segment, "words", None))
        if word_parts:
            parts.extend(word_parts)
            continue

        text = filter_transcribed_caption_text(str(getattr(segment, "text", "") or ""))
        if not text:
            continue

        start_seconds = optional_float(getattr(segment, "start", None)) or 0.0
        end_seconds = optional_float(getattr(segment, "end", None))
        start_ms = max(0, int(start_seconds * 1000))
        end_ms = int(end_seconds * 1000) if end_seconds is not None and end_seconds > start_seconds else start_ms
        parts.append(TimedTextPart(text, start_ms, end_ms, has_word_timing=False))

    return parts


def timed_text_parts_for_words(words: Any) -> list[TimedTextPart]:
    if not words:
        return []

    parts: list[TimedTextPart] = []
    for word in words:
        text = str(getattr(word, "word", getattr(word, "text", "")) or "")
        if not text.strip():
            continue

        start_seconds = optional_float(getattr(word, "start", None))
        end_seconds = optional_float(getattr(word, "end", None))
        if start_seconds is None or end_seconds is None or end_seconds <= start_seconds:
            return []

        parts.append(TimedTextPart(
            text,
            max(0, int(start_seconds * 1000)),
            max(1, int(end_seconds * 1000)),
            has_word_timing=True,
        ))

    return parts


def join_text_parts(parts: list[TimedTextPart]) -> str:
    if not parts:
        return ""

    if all(part.has_word_timing for part in parts):
        return filter_transcribed_caption_text(join_word_timed_text_parts(parts))

    return filter_transcribed_caption_text(" ".join(part.text.strip() for part in parts if part.text.strip()))


def join_word_timed_text_parts(parts: list[TimedTextPart]) -> str:
    output: list[str] = []
    for part in parts:
        text = str(part.text or "")
        if not text.strip():
            continue
        if output and should_insert_word_separator(output[-1], text):
            output.append(" ")
        output.append(text)
    return normalize_transcribed_text("".join(output))


def should_insert_word_separator(previous_text: str, next_text: str) -> bool:
    previous = previous_text.rstrip()
    current = next_text.lstrip()
    if not previous or not current:
        return False
    if previous_text[-1].isspace() or next_text[0].isspace():
        return False

    previous_char = previous[-1]
    current_char = current[0]
    if current_char in ".,!?;:%)]}，。！？、；：」』》）":
        return False
    if previous_char in "([{「『《（":
        return False
    if is_korean_bound_morpheme(current):
        return False

    return character_uses_word_spacing(previous_char) or character_uses_word_spacing(current_char)


def is_korean_bound_morpheme(text: str) -> bool:
    token = normalize_transcribed_text(text)
    if not token or not all(is_korean_syllable(char) for char in token):
        return False
    if token in KOREAN_BOUND_MORPHEMES:
        return True
    return any(token.startswith(prefix) for prefix in KOREAN_BOUND_MORPHEME_PREFIXES)


KOREAN_BOUND_MORPHEMES = {
    "은",
    "는",
    "이",
    "가",
    "을",
    "를",
    "의",
    "도",
    "만",
    "에",
    "엔",
    "에서",
    "에게",
    "한테",
    "께",
    "로",
    "으로",
    "와",
    "과",
    "랑",
    "이랑",
    "하고",
    "부터",
    "까지",
    "보다",
    "처럼",
    "만큼",
    "조차",
    "마저",
    "마다",
    "밖에",
    "뿐",
    "이나",
    "나",
    "라도",
    "든",
    "든지",
    "야",
    "아",
    "여",
    "요",
    "죠",
    "고",
}


KOREAN_BOUND_MORPHEME_PREFIXES = (
    "들",
    "입니다",
    "이에요",
    "예요",
    "합니다",
    "하세요",
    "하죠",
    "하자",
    "하게",
    "하고",
    "해서",
    "하며",
    "하면",
    "하니까",
    "하는",
    "한",
    "할",
    "함",
    "해요",
    "했다",
    "했어",
    "했죠",
    "했네",
    "했고",
    "됩니다",
    "됐",
    "되는",
    "되면",
    "되니까",
    "네요",
    "습니다",
    "습니까",
    "잖아",
)


def is_korean_syllable(character: str) -> bool:
    return 0xAC00 <= ord(character) <= 0xD7A3


def character_uses_word_spacing(character: str) -> bool:
    if not character:
        return False
    codepoint = ord(character)
    if (
        0x3040 <= codepoint <= 0x30FF
        or 0x3400 <= codepoint <= 0x9FFF
        or 0xF900 <= codepoint <= 0xFAFF
        or 0x20000 <= codepoint <= 0x2FA1F
    ):
        return False
    return character.isalnum()


def normalize_transcribed_text(text: str) -> str:
    return " ".join(text.split()).strip()


def filter_transcribed_caption_text(text: Any) -> str:
    normalized = normalize_transcribed_text(str(text or ""))
    if not normalized:
        return ""
    return "" if is_probable_transcription_hallucination_text(normalized) else normalized


def is_probable_transcription_hallucination_text(text: str) -> bool:
    canonical = canonical_transcription_text(text)
    if not canonical:
        return False

    if is_exact_hallucination_text(canonical):
        return True

    if is_outro_thanks_hallucination(canonical):
        return True

    if is_short_hallucination_marker(canonical):
        return True

    return False


def is_exact_hallucination_text(canonical: str) -> bool:
    return canonical in TRANSCRIPTION_HALLUCINATION_EXACT_TEXTS


def is_outro_thanks_hallucination(canonical: str) -> bool:
    for phrase in OUTRO_THANKS_HALLUCINATION_PHRASES:
        if phrase not in canonical:
            continue
        remaining = canonical.replace(phrase, "", 1)
        if not remaining:
            return True
        if has_hallucination_marker(remaining):
            return True
    return False


def is_short_hallucination_marker(canonical: str) -> bool:
    # Standalone channel/news/subtitle markers are usually hallucinations;
    # longer text is kept so real utterances mentioning those words survive.
    return has_hallucination_marker(canonical) and len(canonical) <= 16


def has_hallucination_marker(canonical: str) -> bool:
    return any(marker in canonical for marker in TRANSCRIPTION_HALLUCINATION_MARKERS)


def is_probable_whisper_hallucination(segment: Any) -> bool:
    no_speech_prob = optional_float(getattr(segment, "no_speech_prob", None))
    avg_logprob = optional_float(getattr(segment, "avg_logprob", None))
    compression_ratio = optional_float(getattr(segment, "compression_ratio", None))

    if no_speech_prob is not None and avg_logprob is not None:
        if no_speech_prob >= WHISPER_NO_SPEECH_THRESHOLD and avg_logprob <= WHISPER_LOG_PROB_THRESHOLD:
            return True

    if compression_ratio is not None and compression_ratio >= WHISPER_COMPRESSION_RATIO_THRESHOLD:
        return True

    return False


def optional_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def run_stdio(models_dir: Path) -> int:
    worker = StreamWorker(models_dir)
    emit({"type": "model_status", "stage": "ready", "message": "stdio worker ready", "progress": None})
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            worker.handle(parse_worker_command(line))
        except Exception as exc:
            emit({"type": "error", "code": "worker_exception", "message": str(exc), "recoverable": True})
    return 0


def parse_worker_command(line: str) -> dict[str, Any]:
    return json.loads(line.lstrip("\ufeff"))


def download_models(models_dir: Path) -> int:
    models_dir.mkdir(parents=True, exist_ok=True)
    engine = LocalSpeechEngine(models_dir)
    diarization_enabled = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_ENABLED", "true").lower() in {"1", "true", "yes", "on"}
    diarization_model = normalize_diarization_model(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"))
    engine.configure({
        "type": "configure",
        "inputMode": "system_and_mic",
        "sttModel": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_STT_MODEL", "small"),
        "sttLanguages": [],
        "sttQualityPreset": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_STT_QUALITY_PRESET", 50),
        "diarizationQualityPreset": os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_QUALITY_PRESET", os.environ.get("LIVE_DIALOGUE_TRANSLATOR_STT_QUALITY_PRESET", 50)),
        "computeMode": "auto",
        "asrEngine": normalize_asr_engine(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE")),
        "diarizationEnabled": diarization_enabled,
        "diarizationModel": diarization_model,
        "maxSpeakers": 6,
        "exactSpeakers": None,
        "showLatency": True,
        "speakerNames": {},
    })
    materialize_model_cache_links(models_dir, engine.config.stt_model)
    default_whisper = engine.config.asr_engine == "faster_whisper"
    if default_whisper and not has_whisper_model_bin(models_dir, engine.config.stt_model):
        repair_whisper_cache(models_dir, engine.config.stt_model)
    engine.ensure_loaded()
    if default_whisper and not engine.whisper_model:
        if materialize_model_cache_links(models_dir, engine.config.stt_model):
            engine.whisper_model = None
            engine.ensure_loaded()
    if diarization_enabled and not engine.diarization_pipeline:
        if materialize_model_cache_links(models_dir, engine.config.stt_model):
            engine.diarization_pipeline = None
            engine.ensure_loaded()
    if default_whisper and not engine.whisper_model and should_repair_whisper_cache(engine.last_stt_error):
        repaired = repair_whisper_cache(models_dir, engine.config.stt_model)
        if repaired:
            engine.whisper_model = None
            engine.ensure_loaded()
            if not engine.whisper_model and materialize_model_cache_links(models_dir, engine.config.stt_model):
                engine.whisper_model = None
                engine.ensure_loaded()
    print(f"Model directory: {models_dir}")
    if not engine.whisper_model:
        print("STT model preparation failed. See the error above.", file=sys.stderr)
        return 2
    if diarization_enabled and not engine.diarization_pipeline:
        message = "Diarization model preparation failed."
        if engine.config.diarization_model != "sortformer":
            message += " Accept the required pyannote model terms and save a valid Hugging Face token."
        print(message, file=sys.stderr)
        return 3
    print("Model preparation completed as far as current credentials and Python packages allow.")
    return 0


def check_hf_access(models_dir: Path) -> int:
    diarization_model = normalize_diarization_model(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"))
    if diarization_model == "sortformer":
        emit({
            "type": "model_status",
            "stage": "hf_access_ok",
            "message": f"{diarization_model} does not require pyannote Hugging Face terms.",
            "progress": 1,
        })
        return 0

    token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGINGFACE_TOKEN")
    if not token:
        emit({"type": "error", "code": "hf_token_missing", "message": "Hugging Face token is required for local diarization models.", "recoverable": True})
        return 2

    try:
        from huggingface_hub import hf_hub_download

        model_ids = [DIARIZATION_MODEL_ID]
        cache_dir = models_dir / "pyannote"
        if diarization_model == "diart":
            model_ids = [DIART_SEGMENTATION_MODEL_ID, DIART_EMBEDDING_MODEL_ID]
            cache_dir = models_dir / "diart"

        for model_id in model_ids:
            hf_hub_download(
                repo_id=model_id,
                filename="config.yaml",
                token=token,
                cache_dir=str(cache_dir),
            )
        emit({"type": "model_status", "stage": "hf_access_ok", "message": f"{', '.join(model_ids)} access verified.", "progress": 1})
        return 0
    except Exception as exc:
        diarization_model = normalize_diarization_model(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"))
        emit({"type": "error", "code": "hf_access_denied", "message": hf_access_denied_message(exc, diarization_model), "recoverable": True})
        return 3


class DiartPipelineAdapter:
    outputs_absolute_time = True

    def __init__(self, pipeline: Any, duration: float, step: float) -> None:
        self.pipeline = pipeline
        self.duration = duration
        self.step = step
        self.stream_position_seconds = 0.0
        self._step_buffer = None
        self._rolling_chunk = None

    def __call__(self, audio: dict[str, Any], **_kwargs: Any) -> Any:
        waveform = audio.get("waveform")
        sample_rate = int(audio.get("sample_rate", SAMPLE_RATE))
        reset_state = bool(_kwargs.get("reset", False))
        if reset_state:
            self.reset()

        chunks = self._to_streaming_windows(waveform, sample_rate)
        if not chunks:
            return empty_pyannote_annotation()

        outputs = self.pipeline(chunks)
        return self._merge_outputs(outputs)

    def reset(self) -> None:
        self.stream_position_seconds = 0.0
        self._step_buffer = None
        self._rolling_chunk = None
        reset = getattr(self.pipeline, "reset", None)
        if callable(reset):
            reset()

    def _to_streaming_windows(self, waveform: Any, sample_rate: int) -> list[Any]:
        import numpy as np
        from pyannote.core import SlidingWindow, SlidingWindowFeature

        samples = waveform.detach().cpu().numpy() if hasattr(waveform, "detach") else np.asarray(waveform)
        if samples.ndim == 2:
            samples = samples[0]
        samples = np.asarray(samples, dtype=np.float32).reshape(-1)
        if samples.size == 0:
            return []

        duration_samples = int(round(self.duration * sample_rate))
        step_samples = max(1, int(round(self.step * sample_rate)))
        chunks = []
        self._step_buffer = samples if self._step_buffer is None else np.concatenate([self._step_buffer, samples])
        while self._step_buffer is not None and self._step_buffer.size >= step_samples:
            step_chunk = self._step_buffer[:step_samples]
            remaining = self._step_buffer[step_samples:]
            self._step_buffer = remaining if remaining.size else None

            self._rolling_chunk = (
                step_chunk
                if self._rolling_chunk is None
                else np.concatenate([self._rolling_chunk, step_chunk])
            )
            if self._rolling_chunk.size > duration_samples:
                self._rolling_chunk = self._rolling_chunk[-duration_samples:]
                self.stream_position_seconds += self.step

            if self._rolling_chunk.size != duration_samples:
                continue

            sliding_window = SlidingWindow(
                start=self.stream_position_seconds,
                duration=1.0 / sample_rate,
                step=1.0 / sample_rate,
            )
            chunks.append(SlidingWindowFeature(self._rolling_chunk.reshape(-1, 1), sliding_window))
        return chunks

    @staticmethod
    def _merge_outputs(outputs: Any) -> Any:
        from pyannote.core import Annotation, Segment

        annotation = Annotation()
        track_index = 0
        for output in outputs:
            current = output[0] if isinstance(output, tuple) and output else output
            itertracks = getattr(current, "itertracks", None)
            if not callable(itertracks):
                continue
            for segment, _track, label in itertracks(yield_label=True):
                absolute_start = float(getattr(segment, "start", 0.0))
                absolute_end = float(getattr(segment, "end", absolute_start))
                if absolute_end <= absolute_start:
                    continue
                annotation[Segment(absolute_start, absolute_end), f"diart_{track_index}"] = str(label)
                track_index += 1
        return annotation


class SortformerPipelineAdapter:
    outputs_absolute_time = True

    def __init__(self, shared_model: Any) -> None:
        from whisperlivekit.diarization.sortformer_backend import SortformerDiarizationOnline

        self.pipeline = SortformerDiarizationOnline(shared_model=shared_model, sample_rate=SAMPLE_RATE)

    def __call__(self, audio: dict[str, Any], **_kwargs: Any) -> Any:
        import numpy as np
        from pyannote.core import Annotation, Segment

        waveform = audio.get("waveform")
        samples = waveform.detach().cpu().numpy() if hasattr(waveform, "detach") else np.asarray(waveform)
        if samples.ndim == 2:
            samples = samples[0]
        samples = np.asarray(samples, dtype=np.float32).reshape(-1)
        if samples.size == 0:
            return empty_pyannote_annotation()

        self.pipeline.insert_audio_chunk(samples)
        segments = asyncio.run(self.pipeline.diarize())
        annotation = Annotation()
        for index, segment in enumerate(segments):
            start = float(getattr(segment, "start", 0.0))
            end = float(getattr(segment, "end", start))
            if end <= start:
                continue
            speaker = getattr(segment, "speaker", 0)
            annotation[Segment(start, end), f"sortformer_{index}"] = f"SORTFORMER_{speaker}"
        return annotation

    def reset(self) -> None:
        close = getattr(self.pipeline, "close", None)
        if callable(close):
            close()


def empty_pyannote_annotation():
    from pyannote.core import Annotation

    return Annotation()


def load_diart_pipeline(
    token: str,
    cache_dir: Path,
    device: str,
    quality_preset: int,
    max_speakers: int,
    duration: float | None = None,
    step: float | None = None,
    latency: float | None = None,
    hyper_parameters: dict[str, float] | None = None,
):
    suppress_torchcodec_warning()
    apply_torchaudio_compatibility_shims()
    from diart import SpeakerDiarization, SpeakerDiarizationConfig
    import diart.models as diart_models
    import torch

    cache_dir.mkdir(parents=True, exist_ok=True)
    torch_device = torch.device(device)
    segmentation = diart_models.SegmentationModel(
        lambda: load_diart_pyannote_model(DIART_SEGMENTATION_MODEL_ID, token)
    )
    embedding = diart_models.EmbeddingModel(
        lambda: load_diart_pyannote_model(DIART_EMBEDDING_MODEL_ID, token)
    )
    duration = duration if duration is not None else diart_duration_seconds_for_quality(quality_preset)
    step = step if step is not None else DIART_STREAM_CHUNK_SECONDS
    latency = latency if latency is not None else diart_latency_seconds_for_quality(quality_preset)
    hyper_parameters = hyper_parameters or diart_hyper_parameters_for_quality(quality_preset)
    config = SpeakerDiarizationConfig(
        segmentation=segmentation,
        embedding=embedding,
        duration=duration,
        step=step,
        latency=latency,
        max_speakers=max(1, max_speakers),
        device=torch_device,
        sample_rate=SAMPLE_RATE,
        **hyper_parameters,
    )
    return DiartPipelineAdapter(SpeakerDiarization(config), duration, step)


def load_sortformer_pipeline(max_speakers: int):
    from whisperlivekit.diarization.sortformer_backend import SortformerDiarization

    _ = max_speakers
    return SortformerPipelineAdapter(SortformerDiarization())


def load_diart_pyannote_model(model_id: str, token: str):
    suppress_torchcodec_warning()
    apply_torchaudio_compatibility_shims()
    from diart.models import PowersetAdapter
    from pyannote.audio import Model

    try:
        model = from_pretrained_with_token(Model.from_pretrained, model_id, token)
        specs = getattr(model, "specifications", None)
        if specs is not None and getattr(specs, "powerset", False):
            return PowersetAdapter(model)
        return model
    except Exception:
        if model_id != DIART_EMBEDDING_MODEL_ID:
            raise

        from pyannote.audio.pipelines.speaker_verification import PretrainedSpeakerEmbedding

        def load_embedding():
            try:
                return PretrainedSpeakerEmbedding(model_id, token=token)
            except TypeError as exc:
                if "token" not in str(exc):
                    raise
                return PretrainedSpeakerEmbedding(model_id, use_auth_token=token)

        return call_without_stdout_noise(load_embedding)


def load_pyannote_pipeline(model_id: str, token: str, cache_dir: Path):
    suppress_torchcodec_warning()
    from pyannote.audio import Pipeline

    cache_dir.mkdir(parents=True, exist_ok=True)
    return from_pretrained_with_token(Pipeline.from_pretrained, model_id, token, cache_dir=str(cache_dir))


def pcm_to_waveform(pcm: bytes):
    import numpy as np
    import torch

    samples = np.frombuffer(pcm, dtype="<i2").astype("float32") / 32768.0
    return torch.from_numpy(samples).unsqueeze(0)


def pcm_has_voice(pcm: bytes) -> bool:
    return samples_rms(pcm_to_samples(pcm)) >= VOICE_RMS_THRESHOLD


def should_repair_whisper_cache(error: str | None) -> bool:
    if not error:
        return False
    lowered = error.lower()
    return "model.bin" in lowered or "unable to open file" in lowered


def repair_whisper_cache(models_dir: Path, model_name: str) -> bool:
    cache_dir = whisper_cache_dir(models_dir, model_name)
    if not cache_dir.exists():
        return False
    print(f"Repairing Whisper cache: {cache_dir}", file=sys.stderr)
    shutil.rmtree(cache_dir, ignore_errors=True)
    return True


def has_whisper_model_bin(models_dir: Path, model_name: str) -> bool:
    cache_dir = whisper_cache_dir(models_dir, model_name)
    try:
        snapshots_dir = cache_dir / "snapshots"
        if not cache_dir.exists() or not snapshots_dir.is_dir():
            return False

        for snapshot_dir in snapshots_dir.iterdir():
            if path_exists_without_following_link(snapshot_dir / "model.bin"):
                return True
    except OSError as exc:
        log(f"Whisper cache check failed: {exc}")
    return False


def path_exists_without_following_link(path: Path) -> bool:
    try:
        path.lstat()
        return True
    except FileNotFoundError:
        return False
    except OSError as exc:
        log(f"Whisper cache entry is not readable: {path}: {exc}")
        return False


def whisper_cache_dir(models_dir: Path, model_name: str) -> Path:
    if "/" in model_name:
        normalized = "models--" + model_name.replace("/", "--")
    else:
        normalized = f"models--Systran--faster-whisper-{model_name}"
    return models_dir / "whisper" / normalized


def check_environment(models_dir: Path) -> int:
    stt_model = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_STT_MODEL", "small")
    asr_engine = normalize_asr_engine(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"))
    diarization_enabled = os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_ENABLED", "true").lower() in {"1", "true", "yes", "on"}
    diarization_model = normalize_diarization_model(os.environ.get("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"))
    materialize_model_cache_links(models_dir, stt_model)
    packages = {
        "faster_whisper": has_module("faster_whisper"),
        "pyannote_audio": has_module("pyannote.audio"),
        "diart": has_module("diart"),
        "torch": has_module("torch"),
        "qwen_asr": has_module("qwen_asr"),
        "whisperlivekit": has_module("whisperlivekit"),
        "whisperx": has_module("whisperx"),
    }
    whisper_root = models_dir / "whisper"
    if asr_engine == "qwen3_asr_diarization":
        model_prepared = packages["qwen_asr"]
    elif asr_engine == "whisperlivekit_sortformer":
        model_prepared = packages["whisperlivekit"]
    else:
        model_prepared = any(whisper_root.rglob("*")) if whisper_root.exists() else False
    stt_model_loadable = False
    stt_model_error = None
    if asr_engine == "qwen3_asr_diarization":
        stt_model_loadable = packages["qwen_asr"]
    elif asr_engine == "whisperlivekit_sortformer":
        stt_model_loadable = packages["whisperlivekit"]
    elif packages["faster_whisper"] and model_prepared:
        try:
            from faster_whisper import WhisperModel

            WhisperModel(
                stt_model,
                device="cpu",
                compute_type="int8",
                download_root=str(whisper_root),
                local_files_only=True,
            )
            stt_model_loadable = True
        except Exception as exc:
            stt_model_error = str(exc)
    print(json.dumps({
        "pythonAvailable": True,
        "fasterWhisperAvailable": packages["faster_whisper"],
        "pyannoteAvailable": packages["pyannote_audio"],
        "diartAvailable": packages["diart"],
        "torchAvailable": packages["torch"],
        "qwenAsrAvailable": packages["qwen_asr"],
        "whisperLiveKitAvailable": packages["whisperlivekit"],
        "whisperXAvailable": packages["whisperx"],
        "sttModelPrepared": model_prepared,
        "sttModelLoadable": stt_model_loadable,
        "diarizationModelPrepared": is_diarization_model_prepared(models_dir, diarization_model, diarization_enabled),
        "sttModelError": stt_model_error,
        "sttModel": stt_model,
    }, ensure_ascii=False), flush=True)
    return 0


def is_diarization_model_prepared(models_dir: Path, diarization_model: str, diarization_enabled: bool) -> bool:
    if not diarization_enabled or diarization_model == "sortformer":
        return True
    if diarization_model == "diart":
        hub_cache = models_dir / "huggingface" / "hub"
        return (
            has_huggingface_model_cache(hub_cache, DIART_SEGMENTATION_MODEL_ID)
            and has_huggingface_model_cache(hub_cache, DIART_EMBEDDING_MODEL_ID)
        ) or (
            has_huggingface_model_cache(models_dir / "diart", DIART_SEGMENTATION_MODEL_ID)
            and has_huggingface_model_cache(models_dir / "diart", DIART_EMBEDDING_MODEL_ID)
        )

    return (
        has_huggingface_model_cache(models_dir / "pyannote", DIARIZATION_MODEL_ID)
        or has_huggingface_model_cache(models_dir / "huggingface" / "hub", DIARIZATION_MODEL_ID)
    )


def has_huggingface_model_cache(cache_root: Path, model_id: str) -> bool:
    snapshots_dir = cache_root / ("models--" + model_id.replace("/", "--")) / "snapshots"
    if not snapshots_dir.exists():
        return False
    try:
        for snapshot_dir in snapshots_dir.iterdir():
            if not snapshot_dir.is_dir():
                continue
            for entry in snapshot_dir.rglob("*"):
                if path_exists_without_following_link(entry):
                    return True
    except OSError as exc:
        log(f"Hugging Face cache check failed: {exc}")
    return False


def has_module(name: str) -> bool:
    try:
        return importlib.util.find_spec(name) is not None
    except ModuleNotFoundError:
        return False


def materialize_model_cache_links(models_dir: Path, stt_model: str) -> bool:
    changed = materialize_hf_cache_links(whisper_cache_dir(models_dir, stt_model))
    changed = materialize_hf_cache_links(models_dir / "pyannote") or changed
    changed = materialize_hf_cache_links(models_dir / "diart") or changed
    changed = materialize_hf_cache_links(models_dir / "huggingface") or changed
    return changed


def materialize_hf_cache_links(cache_root: Path) -> bool:
    if not cache_root.exists():
        return False

    changed = False
    for snapshot_root in cache_root.rglob("snapshots"):
        if not snapshot_root.is_dir():
            continue
        for entry in snapshot_root.rglob("*"):
            try:
                if entry.is_symlink() and materialize_symlink(entry):
                    changed = True
            except OSError as exc:
                log(f"Could not inspect cache link {entry}: {exc}")
    return changed


def materialize_symlink(link_path: Path) -> bool:
    target = Path(os.readlink(link_path))
    if not target.is_absolute():
        target = (link_path.parent / target).resolve(strict=True)
    if not target.is_file():
        return False

    link_path.unlink()
    try:
        os.link(target, link_path)
    except OSError:
        shutil.copy2(target, link_path)
    log(f"Materialized Hugging Face cache link: {link_path}")
    return True


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stdio", action="store_true")
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--check-hf-access", action="store_true")
    parser.add_argument("--models", type=Path, required=True)
    args = parser.parse_args()
    configure_huggingface_cache(args.models)

    if args.download:
        return download_models(args.models)
    if args.check:
        return check_environment(args.models)
    if args.check_hf_access:
        return check_hf_access(args.models)
    if args.stdio:
        return run_stdio(args.models)
    parser.error("Use --stdio, --download, --check, or --check-hf-access")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
