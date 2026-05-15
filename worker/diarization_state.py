from __future__ import annotations

from dataclasses import dataclass, field
import math
from typing import Sequence


UNKNOWN_SPEAKER_ID = "speaker_unknown"


@dataclass(frozen=True)
class SpeakerCountPolicy:
    mode: str
    max_speakers: int
    exact_speakers: int | None = None
    inactive_after_ms: int = 60_000
    protected_after_ms: int = 10_000
    pending_confirmations: int = 1
    match_threshold: float = 0.62
    strict_match_threshold: float = 0.70
    unknown_similarity_threshold: float = 0.52
    embedding_update_rate: float = 0.15


@dataclass(frozen=True)
class SpeakerObservation:
    label: str
    start_ms: int
    end_ms: int
    embedding: Sequence[float] | None = None
    confidence: float = 1.0
    source_model: str = "unknown"


@dataclass(frozen=True)
class SpeakerAssignment:
    speaker_id: str
    reason: str
    confidence: float


@dataclass
class SpeakerSlot:
    speaker_id: str
    embedding: list[float] | None
    first_seen_ms: int
    last_seen_ms: int
    total_voice_ms: int = 0
    active: bool = True
    replaced_count: int = 0
    labels: set[str] = field(default_factory=set)


class SpeakerSlotRegistry:
    def __init__(self, policy: SpeakerCountPolicy) -> None:
        self.policy = policy
        self.slots: list[SpeakerSlot] = []
        self.pending: dict[str, int] = {}

    def reset(self) -> None:
        self.slots.clear()
        self.pending.clear()

    def assign(self, observation: SpeakerObservation) -> SpeakerAssignment:
        now_ms = max(observation.start_ms, observation.end_ms)
        self._mark_inactive(now_ms)

        label_slot = self._slot_for_label(observation.label) if observation.embedding is None else None
        if label_slot is not None:
            self._update_slot(label_slot, observation)
            return SpeakerAssignment(label_slot.speaker_id, "matched_label", observation.confidence)

        best_slot, best_score = self._best_slot(observation.embedding)
        if best_slot is not None and best_score >= self.policy.match_threshold:
            self._update_slot(best_slot, observation)
            return SpeakerAssignment(best_slot.speaker_id, "matched_embedding", best_score)

        speaker_limit = max(1, self.policy.exact_speakers or self.policy.max_speakers)
        if len(self.slots) < speaker_limit:
            slot = self._create_slot(observation)
            return SpeakerAssignment(slot.speaker_id, "created_slot", observation.confidence)

        if self.policy.mode == "exact":
            return SpeakerAssignment(UNKNOWN_SPEAKER_ID, "exact_pool_full", max(0.0, best_score))

        if self.policy.mode == "active_max":
            replacement = self._replacement_candidate(now_ms)
            if replacement is not None and self._confirmed_new_voice(observation, best_score):
                self._replace_slot(replacement, observation)
                return SpeakerAssignment(
                    replacement.speaker_id,
                    "replaced_inactive",
                    max(observation.confidence, best_score),
                )

            if best_score < self.policy.unknown_similarity_threshold:
                return SpeakerAssignment(
                    UNKNOWN_SPEAKER_ID,
                    "active_pool_full_unknown",
                    max(0.0, best_score),
                )

        fallback = best_slot.speaker_id if best_slot is not None else UNKNOWN_SPEAKER_ID
        return SpeakerAssignment(fallback, "pool_full_fallback", max(0.0, best_score))

    def _mark_inactive(self, now_ms: int) -> None:
        for slot in self.slots:
            if now_ms - slot.last_seen_ms >= self.policy.inactive_after_ms:
                slot.active = False

    def _best_slot(self, embedding: Sequence[float] | None) -> tuple[SpeakerSlot | None, float]:
        if embedding is None:
            return None, -1.0

        best_slot = None
        best_score = -1.0
        for slot in self.slots:
            score = cosine_similarity(embedding, slot.embedding)
            if score > best_score:
                best_slot = slot
                best_score = score
        return best_slot, best_score

    def _slot_for_label(self, label: str) -> SpeakerSlot | None:
        label_key = stable_label_key(label)
        if not label_key:
            return None
        for slot in self.slots:
            if label_key in slot.labels:
                return slot
        return None

    def _create_slot(self, observation: SpeakerObservation) -> SpeakerSlot:
        slot = SpeakerSlot(
            speaker_id=f"speaker_{len(self.slots) + 1}",
            embedding=list(observation.embedding) if observation.embedding is not None else None,
            first_seen_ms=observation.start_ms,
            last_seen_ms=observation.end_ms,
            total_voice_ms=max(0, observation.end_ms - observation.start_ms),
            labels={stable_label_key(observation.label)} if stable_label_key(observation.label) else set(),
        )
        self.slots.append(slot)
        return slot

    def _update_slot(self, slot: SpeakerSlot, observation: SpeakerObservation) -> None:
        slot.active = True
        slot.last_seen_ms = max(slot.last_seen_ms, observation.end_ms)
        slot.total_voice_ms += max(0, observation.end_ms - observation.start_ms)
        label_key = stable_label_key(observation.label)
        if label_key:
            slot.labels.add(label_key)
        if observation.embedding is not None:
            slot.embedding = blend_embeddings(
                slot.embedding,
                observation.embedding,
                self.policy.embedding_update_rate,
            )

    def _replacement_candidate(self, now_ms: int) -> SpeakerSlot | None:
        candidates = [
            slot
            for slot in self.slots
            if not slot.active and now_ms - slot.last_seen_ms >= self.policy.protected_after_ms
        ]
        if not candidates:
            return None
        return min(candidates, key=lambda slot: (slot.last_seen_ms, slot.total_voice_ms))

    def _confirmed_new_voice(self, observation: SpeakerObservation, best_score: float) -> bool:
        if best_score >= self.policy.unknown_similarity_threshold:
            return False

        key = stable_pending_key(observation.embedding, observation.label)
        self.pending[key] = self.pending.get(key, 0) + 1
        return self.pending[key] >= max(1, self.policy.pending_confirmations)

    def _replace_slot(self, slot: SpeakerSlot, observation: SpeakerObservation) -> None:
        slot.embedding = list(observation.embedding) if observation.embedding is not None else None
        slot.first_seen_ms = observation.start_ms
        slot.last_seen_ms = observation.end_ms
        slot.total_voice_ms = max(0, observation.end_ms - observation.start_ms)
        slot.active = True
        slot.replaced_count += 1
        label_key = stable_label_key(observation.label)
        slot.labels = {label_key} if label_key else set()


def cosine_similarity(left: Sequence[float] | None, right: Sequence[float] | None) -> float:
    if left is None or right is None or len(left) != len(right) or not left:
        return -1.0

    dot = sum(float(a) * float(b) for a, b in zip(left, right))
    left_norm = math.sqrt(sum(float(a) * float(a) for a in left))
    right_norm = math.sqrt(sum(float(b) * float(b) for b in right))
    if left_norm <= 0 or right_norm <= 0:
        return -1.0
    return dot / (left_norm * right_norm)


def blend_embeddings(current: Sequence[float] | None, incoming: Sequence[float], rate: float) -> list[float]:
    if current is None or len(current) != len(incoming):
        return [float(value) for value in incoming]

    clamped_rate = max(0.0, min(1.0, rate))
    return [
        (float(old) * (1.0 - clamped_rate)) + (float(new) * clamped_rate)
        for old, new in zip(current, incoming)
    ]


def stable_pending_key(embedding: Sequence[float] | None, label: str) -> str:
    if label:
        return f"label:{label}"

    if embedding is None:
        return "label:"

    rounded = ",".join(f"{float(value):.2f}" for value in embedding[:8])
    return f"embedding:{rounded}"


def stable_label_key(label: str) -> str:
    return str(label or "").strip()
