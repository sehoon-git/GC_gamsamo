"""Deterministic integration fixture. This is NOT a language-quality evaluator."""
import json
import re


class MockLLM:
    def __init__(self, settings):
        self.config = {**settings.model, "revision": "mock-fixture-v4"}

    def count(self, messages, role):
        return sum(len(m["content"]) for m in messages) // 2

    def fits(self, messages, role):
        return self.count(messages, role) + self.config["roles"][role]["max_tokens"] + 128 < self.config["max_model_len"]

    async def health(self):
        return {"ready": True, "model": "mock-fixture-v4"}

    async def close(self):
        pass

    async def generate(self, role, messages, schema, metrics, validate=None):
        metrics.calls += 1
        text = messages[-1]["content"]
        if role == "controller":
            text = text.split("분류할 현재 상담자 발언:\n", 1)[-1]
            value = {"reason": "연결 검증용 맥락", "topic_label": "현재 마음", "disclose_fact_ids": []}
        elif role == "patient":
            policy = json.JSONDecoder().raw_decode(messages[0]["content"].split("설정과 현재 상태:\n", 1)[1])[0]
            name = policy["fixed_character"]["name"]
            value = {"text": "면접 생각을 하면 아직 긴장돼요. 지금은 제 마음을 천천히 이야기하고 싶어요.", "disclosed_fact_ids": []}
            if name != "서윤":
                value["text"] = f"{name}입니다. 제 이야기를 천천히 해 보고 싶어요."
            visit = re.search(r"지금은 (\d+)번째 상담", text) if "[대화 시작 제어 신호" in text else None
            if visit and int(visit.group(1)) > 1:
                # Test fixture only; each revisit must pass the repeated-opening guard.
                value["text"] = f"지난 상담에서 꺼낸 마음을 조금 더 이야기해 보고 싶어요. 이번이 {visit.group(1)}번째 방문이네요."
        elif role == "evaluation":
            value = {key: {"score": 0, "explanation": "모의 연결 결과입니다. 실제 상담 품질을 평가한 점수가 아닙니다.", "evidence": []} for key in schema.model_fields}
        elif role == "summary":
            source = json.loads(text)["new_records"][-1]
            value = {"items": [{"category": "fact", "speaker": source["speaker"], "text": source["text"][:100], "source_ids": [source["id"]]}]}
        else:
            value = {"criteria": []}
        result = schema.model_validate(value)
        if validate:
            validate(result)
        return result
