"""Compute agreed human-review gates. No ratings are inferred or fabricated."""
import argparse
import json
from pathlib import Path
from statistics import mean


def grade(mapping, review):
    if mapping['review_id'] != review['review_id']:
        raise ValueError('Review and blind mapping IDs differ')
    labels = [mapping['baseline'], mapping['candidate']]
    values = {label: {'scores': [], 'preferences': 0, 'violations': []} for label in labels}
    if set(review['ratings']) != set(mapping['mapping']):
        raise ValueError('Every blind pair must have a rating')
    for key, pair in mapping['mapping'].items():
        rating = review['ratings'][key]
        preference = rating.get('preference')
        if preference not in {'A','B','tie'}:
            raise ValueError('Missing preference: '+key)
        for side in ['A','B']:
            label = pair[side]
            scores = [rating[side].get(k) for k in ['natural','relevance','character']]
            if any(type(s) is not int or not 1 <= s <= 5 for s in scores):
                raise ValueError('Missing or invalid 1–5 rating: '+key)
            values[label]['scores'].extend(scores)
            values[label]['preferences'] += .5 if preference=='tie' else float(preference==side)
            for flag in ['role_error','fabrication','leak']:
                if type(rating[side].get(flag,False)) is not bool:
                    raise ValueError('Invalid violation flag: '+key)
                if rating[side].get(flag):
                    values[label]['violations'].append({'pair':key,'type':flag})
    result = {}
    count = len(mapping['mapping'])
    if not count:
        raise ValueError('No reviewable pairs')
    for label, value in values.items():
        average = mean(value['scores'])
        preference = value['preferences']/count
        result[label] = {'mean_score': average, 'preference': preference, 'violations': value['violations'],
                         'human_gate_passed': average >= 4 and preference >= .6 and not value['violations']}
    return result


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('mapping',type=Path)
    parser.add_argument('ratings',type=Path)
    args=parser.parse_args()
    result=grade(json.loads(args.mapping.read_text('utf-8')),json.loads(args.ratings.read_text('utf-8')))
    print(json.dumps(result,ensure_ascii=False,indent=2))
    print('Model adoption additionally requires runtime, rule, latency and full-session checks.')


if __name__=='__main__':
    main()
