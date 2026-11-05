import hashlib
import json

from app.core.config import Settings, ExperimentError
from app.schemas.models import CONTENT_VERSION, CaseState, records


class Catalog:
    def __init__(self, settings: Settings):
        self.patient = json.loads(settings.patient_path.read_text("utf-8"))
        if self.patient["content_version"] != CONTENT_VERSION:
            raise RuntimeError("Patient content version does not match the application.")
        self.facts = {f["id"]: f for f in self.patient["private_facts"]}
        self.rubric = {r["id"]: r for r in self.patient["rubric"]}
        self.prompts = {p.stem: p.read_text("utf-8") for p in sorted(settings.prompts.glob("*.txt"))}
        self.prompt_version = hashlib.sha256(json.dumps(self.prompts, ensure_ascii=False, sort_keys=True).encode()).hexdigest()[:16]
        dialogue_prompts = {key: self.prompts[key] for key in ("controller", "patient", "summary")}
        self.dialogue_prompt_version = hashlib.sha256(json.dumps(dialogue_prompts, ensure_ascii=False, sort_keys=True).encode()).hexdigest()[:16]

    def validate_state(self, state: CaseState):
        CaseState.model_validate(state.model_dump())
        if not set(state.revealed_fact_ids) <= self.facts.keys():
            raise ExperimentError("알 수 없는 공개 정보 ID가 있습니다.")
        candidates = {f for session in state.sessions for turn in session.turns for f in turn.candidate_fact_ids}
        if not candidates <= self.facts.keys() or not set(state.revealed_fact_ids) <= candidates:
            raise ExperimentError("공개 확정 정보는 대화에 기록된 후보에서 선택해야 합니다.")
        record_map = {r.id: r for r in records(state)}
        covered_ids = set()
        if state.memory.through_turn_id:
            for record in record_map.values():
                covered_ids.add(record.id)
                if record.id == state.memory.through_turn_id + ":patient":
                    break
        for item in state.memory.items:
            if not set(item.source_ids) <= covered_ids:
                raise ExperimentError("기억의 출처가 요약된 원문 범위에 없습니다.")
            if item.speaker != "both" and any(record_map[i].speaker != item.speaker for i in item.source_ids):
                raise ExperimentError("기억의 발언자와 출처가 일치하지 않습니다.")
        session_order = {session.id: i for i, session in enumerate(state.sessions)}
        for session in state.sessions:
            e = session.evaluation
            if e is None:
                continue
            if len({s.criterion_id for s in e.scores}) != 8 or {s.criterion_id for s in e.scores} != self.rubric.keys():
                raise ExperimentError("저장된 평가 항목이 일치하지 않습니다.")
            for score in e.scores:
                axis = self.rubric[score.criterion_id]["axis"]
                if score.score > 0 and not any(not citation.counter for citation in score.evidence):
                    raise ExperimentError("저장된 양수 점수에 지지 근거가 없습니다.")
                for citation in score.evidence:
                    record = record_map.get(citation.message_id)
                    if record is None or not citation.quote.strip() or citation.quote not in record.text:
                        raise ExperimentError("저장된 평가 근거가 원문과 일치하지 않습니다.")
                    if record.speaker != axis or (axis == "counselor" and record.session_id != session.id):
                        raise ExperimentError("저장된 평가 근거의 발언자 또는 상담 범위가 다릅니다.")
                    if session_order[record.session_id] > session_order[session.id]:
                        raise ExperimentError("이전 상담의 평가가 이후 상담의 발언을 참조합니다.")
            totals = {axis: sum(s.score for s in e.scores if self.rubric[s.criterion_id]["axis"] == axis) for axis in ["patient", "counselor"]}
            passed = all(v >= self.patient["clear_threshold"] for v in totals.values())
            if (e.patient_score, e.counselor_score, e.cleared) != (totals["patient"], totals["counselor"], passed):
                raise ExperimentError("저장된 평가 합산 또는 클리어 상태가 올바르지 않습니다.")
