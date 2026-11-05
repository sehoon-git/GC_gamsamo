"""One-line terminal client. All saves belong to this client, never the API."""
import argparse
import asyncio

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, Settings
from app.schemas.models import CaseState, next_session


HELP = """명령:
  /help              도움말
  /status            실험·상담·질문 사용량·상담 단계
  /retry             저장 또는 요청을 같은 ID로 재시도
  /finish            저장된 5번째 답변에서 마무리·평가 이어가기
  /evaluate          마무리 저장 후 평가만 재시도
  /next              기준 미달 후 기억을 이어 재상담
  /new               기록을 보존하고 초기 서윤으로 새 실험
  /cases             보관된 실험 ID 목록
  /open 실험_ID      보관된 실험의 자동 저장 불러오기
  x, /quit           프로그램 종료 (확정된 대화는 자동 저장됨)
"""


def status(client):
    state, session = client.state, client.state.current
    print(f"[질문 {session.questions_used}/5 · {session.phase} · 상담 {len(state.sessions)}회차]")


def show_evaluation(value, catalog):
    print(f"\n상담 결과: 서윤의 변화 {value.patient_score}/100 · 상담자의 대응 {value.counselor_score}/100")
    for item in value.scores:
        print(f"- {catalog.rubric[item.criterion_id]['label']}: {item.score}/25 — {item.explanation}")
        for source in item.evidence:
            print(f"  {'반대' if source.counter else '지지'} 근거 [{source.message_id}]: {source.quote}")
    print("클리어했습니다. /new로 새 실험을 시작할 수 있습니다." if value.cleared else "기준 미달입니다. /next로 기억을 이어 재상담할 수 있습니다.")
    print(f"[평가 {value.metrics.seconds:.1f}초 · 모델 요청 {value.metrics.calls}회]\n")


async def main():
    parser = argparse.ArgumentParser(description="서윤 1인 상담 실험")
    parser.add_argument("--case", help="보관된 실험 ID")
    parser.add_argument("--new", action="store_true", help="이전 기록을 보존하고 새 형식으로 시작")
    args = parser.parse_args()
    settings, store = Settings.from_env(), SaveStore()
    state = CaseState() if args.new else store.load(case_id=args.case) or CaseState()
    client = SessionClient(settings, store, state)
    store.save(state)
    print("서윤 · 대학 졸업 후 3년째 취업 준비 중. 카페에서 생활비를 벌고 있습니다.")
    print("면접을 앞두면 잠을 못 자고, 최근 예정된 면접에 가지 않아 상담에 왔습니다.")
    print(f"실험: {state.case_id}\n저장 위치: {store.directory}\n/help로 명령을 볼 수 있습니다. x를 입력하고 Enter를 누르면 종료합니다.")
    if state.current.turns:
        print(f"\n마지막 서윤 발언: {state.current.turns[-1].complete_reply}")
    else:
        print("플레이어가 먼저 인사하세요.")
    if state.current.evaluation:
        show_evaluation(state.current.evaluation, client.catalog)
    status(client)

    async def assess():
        print("상담은 저장되었습니다. 결과를 분석합니다…")
        value = await client.evaluate()
        show_evaluation(value, client.catalog)

    async def advance():
        if client.state.current.phase == "closing":
            print("다섯 번째 답변을 저장했습니다. 마무리를 기다립니다…")
            print("\n서윤 >", await client.finish())
        if client.state.current.phase == "pending_evaluation":
            await assess()

    try:
        try:
            await advance()
        except (ExperimentError, ValueError, OSError) as exc:
            print(f"오류: {exc}\n/retry 또는 /finish로 이어갈 수 있습니다.")
        while True:
            try:
                text = input("\n상담자 > ").strip()
                if not text:
                    continue
                if text in {"x", "X", "/quit", "/exit"}:
                    print("대화 프로그램을 종료합니다. 확정된 대화는 저장되어 있습니다.")
                    break
                if text == "/help":
                    print(HELP)
                elif text == "/status":
                    print(f"실험 {client.state.case_id}\n상담 {client.state.current.id}")
                    status(client)
                elif text == "/cases":
                    print("\n".join(store.cases()))
                elif text.startswith("/open "):
                    client.replace(store.load(case_id=text.split(maxsplit=1)[1]))
                    status(client)
                elif text == "/new":
                    client.replace(CaseState())
                    print("이전 실험 기록을 보존했습니다. 새 실험:", client.state.case_id)
                    status(client)
                elif text == "/next":
                    client.replace(next_session(client.state))
                    print("이전 기억을 이어 새 상담을 시작합니다.")
                    status(client)
                elif text == "/finish":
                    await advance()
                elif text == "/evaluate":
                    if not client.state.current.closed_at:
                        raise ValueError("아직 상담이 끝나지 않았습니다.")
                    await assess()
                elif text == "/retry":
                    await client.retry()
                    if client.state.current.turns:
                        print("\n서윤 >", client.state.current.turns[-1].complete_reply)
                    await advance()
                    status(client)
                elif text.startswith("/"):
                    print("알 수 없는 명령입니다. /help를 입력하세요.")
                else:
                    if client.state.current.phase != "active" or client.state.cleared:
                        raise ValueError("상담이 종료되었습니다. /evaluate, /next 또는 /new를 사용하세요.")
                    print("서윤의 답변을 기다립니다…")
                    turn = await client.turn(text)
                    print("\n서윤 >", turn.complete_reply)
                    status(client)
                    print(f"[{turn.metrics.seconds:.1f}초 · 모델 요청 {turn.metrics.calls}회]")
                    await advance()
            except (EOFError, KeyboardInterrupt):
                print("\n확정된 대화는 자동 저장되어 있습니다.")
                break
            except (ExperimentError, ValueError, OSError) as exc:
                print(f"오류: {exc}")
                if client.pending_request or client.pending_response:
                    print("/retry로 같은 요청을 이어갑니다. 받은 응답이 있으면 저장만 재시도합니다.")
                elif client.state.current.closed_at and not client.state.current.evaluation:
                    print("상담 기록은 저장되어 있습니다. /evaluate로 평가만 재시도하세요.")
    finally:
        await client.close()


if __name__ == "__main__":
    asyncio.run(main())
