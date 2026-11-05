"""Generate a local, model-blind comparison page from matched benchmark runs."""
import argparse
import hashlib
import json
from pathlib import Path
import random

PAGE = r'''<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>서윤 대화 블라인드 검토</title>
<style>body{font:17px/1.6 system-ui,sans-serif;background:#f3f1eb;color:#24322d;max-width:1100px;margin:auto;padding:28px}h1{font-size:28px}.sample{background:white;border-radius:14px;padding:24px;margin:24px 0;border:1px solid #d9ded8}.pair{display:grid;grid-template-columns:1fr 1fr;gap:22px}.answer{background:#f6f8f5;border-radius:10px;padding:18px}select{font:inherit;padding:6px;margin:5px}button{font:inherit;padding:12px 20px;background:#245843;color:white;border:0;border-radius:8px;cursor:pointer}label{display:block}.sticky{position:sticky;top:0;background:#f3f1eb;padding:12px;box-shadow:0 2px 4px #ccc}small{color:#52645c}@media(max-width:720px){.pair{grid-template-columns:1fr}}</style>
<h1>서윤의 한국어 대화 검토</h1><p>같은 상황의 A·B 답변을 읽고 자연스러움·질문 관련성·캐릭터 유지를 각각 1~5점으로 평가해 주세요. 역할 혼동, 설정 날조, 허용되지 않은 비밀 공개가 있으면 표시합니다. 모델 이름은 숨겼고 A·B 위치는 항목마다 섞었습니다.</p>
<details><summary>캐릭터와 검토 기준</summary><p>서윤은 대학 졸업 후 3년째 구직 중이며 생활비를 위해 카페에서 일합니다. 면접 전 잠을 못 자고 최근 예정된 면접에 가지 않았습니다. 부모의 채용 정보가 부담스럽고 취업한 친구 모임을 피합니다. 처음에는 자기비난과 취업 도움에 대한 기대가 있습니다. 칭찬 한 번으로 완치되거나 상담 밖 계획을 실행했다고 말하면 안 됩니다. 회사명·가족 이름·나이·성별 등은 미정입니다.</p><p>면접 부담을 수용하며 탐색하면 회피 뒤 편안함을, 친구 감정을 수용하며 탐색하면 친구가 같은 회사에 합격한 일과 질투를, 카페에서 해낸 일을 구체적으로 물으면 신입 교육 경험을 말할 수 있습니다. 이전 설정에 없는 주요 사건과 상담자 역할 전환은 오류입니다. 사실 ID 검사만으로 의미 오류가 검증되지는 않습니다.</p><p>1 매우 부적절 · 2 어색함이 큼 · 3 수정이 필요 · 4 시연에 적절 · 5 매우 자연스러움. 동률은 선호도 0.5표로 계산합니다. 답변을 제공하지 못한 후보는 그 상황의 품질 비교에 유리하게 제외하지 않고 실패로 별도 표시합니다.</p></details>
<div class="sticky"><button id="download">평가 JSON 저장</button> <span id="progress"></span></div><main id="samples"></main>
<script>const samples=__DATA__;const reviewId=__ID__;const saved=JSON.parse(localStorage.getItem(reviewId)||'{}');const main=document.getElementById('samples');
function element(tag,text,parent,cls){const node=document.createElement(tag);if(text!==undefined)node.textContent=text;if(cls)node.className=cls;if(parent)parent.append(node);return node;}
function update(){localStorage.setItem(reviewId,JSON.stringify(saved));let complete=samples.filter(s=>['A','B'].every(side=>['natural','relevance','character'].every(k=>saved[s.id]?.[side]?.[k]))&&saved[s.id]?.preference).length;document.getElementById('progress').textContent=`${complete}/${samples.length}개 완료 · 이 브라우저에 임시 저장`;}
function select(parent,key,values,callback,selected){const label=element('label',key,parent);const control=element('select',undefined,label);element('option','선택',control).value='';for(const [value,text] of values)element('option',text,control).value=value;control.value=selected||'';control.onchange=()=>{callback(control.value);update()};}
for(const sample of samples){saved[sample.id] ||= {A:{},B:{},preference:''};const card=element('article',undefined,main,'sample');element('h2',sample.id+' · '+sample.focus,card);if(sample.context.length)element('p',sample.context.map(x=>'상담자: '+x[0]+'\n서윤: '+x[1]).join('\n'),card);element('p','현재 입력: '+sample.input,card);const pair=element('div',undefined,card,'pair');for(const side of ['A','B']){const box=element('section',undefined,pair,'answer');element('h3',side,box);element('p',sample[side],box);for(const [key,title] of [['natural','자연스러움'],['relevance','답변 관련성'],['character','캐릭터 유지']])select(box,title,[1,2,3,4,5].map(x=>[x,x+'점']),value=>saved[sample.id][side][key]=Number(value)||null,saved[sample.id][side][key]);for(const [key,title] of [['role_error','역할 혼동'],['fabrication','설정 날조'],['leak','허용되지 않은 정보 공개']]){const label=element('label',undefined,box);const check=element('input',undefined,label);check.type='checkbox';check.checked=!!saved[sample.id][side][key];label.append(document.createTextNode(' '+title));check.onchange=()=>{saved[sample.id][side][key]=check.checked;update()};}}select(card,'더 선호하는 답변',[['A','A'],['B','B'],['tie','동률']],value=>saved[sample.id].preference=value,saved[sample.id].preference);}
update();document.getElementById('download').onclick=()=>{const data={review_id:reviewId,reviewed_at:new Date().toISOString(),ratings:saved};const link=document.createElement('a');link.href=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)],{type:'application/json'}));link.download='seoyun-human-ratings.json';link.click();URL.revokeObjectURL(link.href)};</script></html>'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    runs = [json.loads(p.read_text("utf-8")) for p in [args.baseline,args.candidate]]
    for key in ["scenarios_sha256","dialogue_prompt_version"]:
        if runs[0][key] != runs[1][key]:
            raise SystemExit("Do not compare differing scenarios/prompts: "+key)
    indexes=[{(s["scenario"],s["repeat"]):s for s in r["samples"]} for r in runs]
    if set(indexes[0])!=set(indexes[1]) or len(indexes[0])!=40:
        raise SystemExit("Both runs must contain the same 40 samples")
    generator=random.Random(20261106)
    samples,mapping=[],{}
    for key,left in indexes[0].items():
        right=indexes[1][key]
        order=[0,1];generator.shuffle(order)
        sample_id=f"Q{len(samples)+1:02d}"
        pair=[indexes[i][key] for i in order]
        samples.append({"id":sample_id,"focus":left['focus'],"input":left['input'],"context":left['context'],
                        "A":pair[0].get('reply','[응답 실패: '+pair[0].get('error','unknown')+']'),
                        "B":pair[1].get('reply','[응답 실패: '+pair[1].get('error','unknown')+']')})
        mapping[sample_id]={"scenario":key[0],"repeat":key[1],"A":runs[order[0]]["label"],"B":runs[order[1]]["label"]}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    review_id='seoyun-'+hashlib.sha256(json.dumps(samples,ensure_ascii=False,sort_keys=True).encode()).hexdigest()[:20]
    data=json.dumps(samples,ensure_ascii=False).replace('<','\\u003c')
    args.output.write_text(PAGE.replace('__DATA__',data).replace('__ID__',json.dumps(review_id)),"utf-8")
    args.output.with_suffix('.mapping.json').write_text(json.dumps({"review_id":review_id,"baseline":runs[0]['label'],"candidate":runs[1]['label'],"mapping":mapping},ensure_ascii=False,indent=2),"utf-8")
    print(f"Created {len(samples)} blind pairs. Do not share mapping until ratings are complete.")


if __name__ == '__main__':
    main()
