"""Spoken reply budget, independent of the larger evaluation/summary budgets.

Tokens include the JSON envelope and disclosure IDs. Character validation is
separate because Korean characters and model tokens do not have a fixed ratio.
Existing transcript fields deliberately keep their original, unlimited schema.
"""
PATIENT_MAX_OUTPUT_TOKENS = 192
PATIENT_MAX_CHARACTERS = 60

# Do not force a grammar length boundary: it can close the JSON string in the
# middle of a Korean word. Validate length after a naturally finished response.
PATIENT_TEXT_PATTERN = r"^[^\u0000-\u001f\u007f-\u009f\u200b-\u200f\u2028-\u202e\u2060\ufeff\u4e00-\u9fff\u3040-\u30ff]+$"
