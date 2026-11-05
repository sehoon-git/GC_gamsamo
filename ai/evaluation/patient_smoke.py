"""Real-model patient identity, dialogue, storage and evaluation smoke; outputs stay local."""
import argparse
import asyncio
import json
from pathlib import Path
import tempfile
from time import perf_counter

import httpx

from app.client import SaveStore, SessionClient
from app.core.config import ROOT, Settings
from app.schemas.models import CaseState, PATIENT_IDS, new_id, now

SCENARIOS = {
    'seoyun': ['요즘 가장 부담스러운 일은 무엇인가요?', '카페에서 해내고 있는 일도 함께 살펴보고 싶어요.'],
    'harin': ['도와드리고 싶은 마음과 내 일정이 밀려 답답한 마음이 함께 있을 수 있겠어요. 어머니와 편하게 지냈던 시간도 있을까요?', '발표를 맡거나 맡지 않는 것 모두 하린 씨가 정할 수 있어요. 어떤 경험을 어디까지 나누고 싶은가요?'],
    'dohyeon': ['급여를 비교하다 보면 지금까지의 선택까지 의심하게 될 수 있겠어요. 현재 생활에서 소중하거나 놓치기 아쉬운 것은 무엇인가요?', '두 회사의 급여뿐 아니라 주거비, 업무와 근무시간, 관계까지 같이 살펴볼까요? 면접에서 먼저 확인하고 싶은 조건이 있나요?'],
    'taejun': ['평가받는 압박이 가족과 보좌진에게까지 이어져 마음이 쓰이셨겠어요. 비판의 내용과 지역을 묶어 판단하는 말은 어떻게 다르게 느껴지나요?', '협력 여부를 제가 정하기보다 태준 씨가 중요하게 생각하는 기준을 듣고 싶어요. 보좌진에게 날카롭게 대한 일은 어떻게 다루고 싶으세요?'],
    'yeonwoo': ['아이가 신났던 이야기를 듣고 싶으면서 그 순간에 함께하지 못해 서운할 수도 있겠어요. 그때 연우 씨 마음은 어땠나요?', '그 서운함이 다시 같이 살고 싶다는 뜻이라고 단정하지 않을게요. 아이에게 달래 달라고 하지 않고 이 마음을 나눌 수 있는 어른이나 공간이 있을까요?'],
}

async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--api-url', default='http://127.0.0.1:8001')
    parser.add_argument('--patients', nargs='+', choices=PATIENT_IDS, default=list(PATIENT_IDS))
    parser.add_argument('--game-progress-id', default=new_id('validation'))
    parser.add_argument('--label', default='characters')
    args = parser.parse_args()
    if not args.label.replace('-', '').replace('_', '').isalnum():
        raise SystemExit('Use an alphanumeric label.')
    settings = Settings.from_env()
    report = ROOT / 'ai/outputs' / (args.label + '-patient-smoke.json')
    results = []
    headers = {'Authorization': 'Bearer '+settings.api_key} if settings.api_key else {}
    async with httpx.AsyncClient(base_url=args.api_url, headers=headers, timeout=180) as http:
        h = await http.get('/health');h.raise_for_status()
        assert h.json()['mode'] == 'model' and h.json()['inference']['ready']
        for patient_id in args.patients:
            result = {'patient_id':patient_id, 'checks':[], 'ok':False}
            results.append(result)
            started = perf_counter()
            try:
                with tempfile.TemporaryDirectory(prefix='gamsamo-patient-') as temporary:
                    payload = dict(patient_id=patient_id, game_progress_id=args.game_progress_id, case_id=new_id('case'), session_id=new_id('session'), request_id=new_id('opening'), created_at=now())
                    response = await http.post('/v1/dialogue/start', json=payload);response.raise_for_status()
                    state = CaseState.model_validate(response.json()['state'])
                    store = SaveStore(Path(temporary), patient_id=patient_id, game_progress_id=args.game_progress_id)
                    store.save(state)
                    client = SessionClient(settings, store, state, http=http)
                    result['checks'].append('opening')
                    for text in SCENARIOS[patient_id]:
                        await client.turn(text)
                    result['checks'].append('two_freeform_turns')
                    client = SessionClient(settings, store, store.load(), http=http)
                    await client.finish()
                    evaluation = await client.evaluate()
                    client.catalog.validate_state(store.load())
                    result['checks'].extend(['restore', 'fixed_close', 'evaluation', 'exact_evidence'])
                    result['state'] = client.state.model_dump(mode='json')
                    if not evaluation.cleared:
                        response = await http.post('/v1/dialogue/revisit', json=dict(state=result['state'], session_id=new_id('session'), request_id=new_id('opening'), created_at=now()))
                        response.raise_for_status()
                        next_state = CaseState.model_validate(response.json()['state'])
                        client.catalog.validate_state(next_state)
                        assert next_state.sessions[:-1] == client.state.sessions
                        assert next_state.game_progress_id == args.game_progress_id
                        assert next_state.revealed_fact_ids == client.state.revealed_fact_ids
                        store.save(next_state)
                        assert store.load().current.id == next_state.current.id
                        result['revisit_state'] = next_state.model_dump(mode='json')
                        result['checks'].append('revisit')
                    result['ok'] = True
            except Exception as exc:
                result['error_type'] = type(exc).__name__
                # Details and raw conversation are private, never print them or add them to a PR.
                result['error'] = str(exc)
            finally:
                result['seconds'] = perf_counter()-started
                report.write_text(json.dumps(results, ensure_ascii=False, indent=2)+'\n')
                print(patient_id, 'PASS' if result['ok'] else 'FAIL', ','.join(result['checks']), flush=True)
    if not all(r['ok'] for r in results):
        raise SystemExit('Patient smoke failed; inspect the private report.')

if __name__ == '__main__':
    asyncio.run(main())
