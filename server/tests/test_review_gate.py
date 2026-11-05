"""Human-review acceptance must not infer missing grades or hide violations."""
import importlib.util
from pathlib import Path

import pytest

PATH = Path(__file__).resolve().parents[2] / 'ai/evaluation/grade_review.py'
spec = importlib.util.spec_from_file_location('grade_review', PATH)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def fixture():
    mapping={'review_id':'test','baseline':'old','candidate':'new','mapping':{}}
    review={'review_id':'test','ratings':{}}
    for i in range(5):
        key=f'Q{i}'
        mapping['mapping'][key]={'A':'new','B':'old'}
        review['ratings'][key]={'A':{'natural':4,'relevance':4,'character':4},
            'B':{'natural':3,'relevance':3,'character':3},'preference':'A' if i<3 else 'B'}
    return mapping,review


def test_review_threshold_and_violation_are_independent():
    mapping,review=fixture()
    result=module.grade(mapping,review)['new']
    assert result['human_gate_passed'] and result['preference']==.6
    review['ratings']['Q0']['A']['fabrication']=True
    assert not module.grade(mapping,review)['new']['human_gate_passed']
    review['ratings']['Q0']['A']['fabrication']=False
    review['ratings']['Q2']['preference']='B'
    assert not module.grade(mapping,review)['new']['human_gate_passed']


def test_incomplete_or_mismatched_human_review_cannot_pass():
    mapping,review=fixture()
    del review['ratings']['Q0']['A']['natural']
    with pytest.raises(ValueError,match='rating'):
        module.grade(mapping,review)
    mapping,review=fixture()
    review['review_id']='different'
    with pytest.raises(ValueError,match='IDs'):
        module.grade(mapping,review)
