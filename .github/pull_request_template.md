## What this changes

<!-- The behaviour that differs afterwards, not a list of the files you touched. -->

## Why

<!-- What was wrong, or what could not be done before. -->

## How it was verified

<!-- Which suite, which real file, which numbers moved. "Tests pass" on its own
     does not say much: all five suites pass on main already. -->

- [ ] `python tools/smoke-test.py` (13 checks)
- [ ] `python tools/acceptance-test.py` (65)
- [ ] `python tools/scheduler-test.py` (45)
- [ ] `python tools/planning-test.py` (52)
- [ ] `python tools/robustness-test.py` (34)
- [ ] `python tools/packaging-test.py`
- [ ] `python tools/project-engine-test.py` — on a machine with Microsoft Project, if scheduling or COM changed
- [ ] Tried against a real schedule, not only a generated one

## Contract check

- [ ] No write is reported as applied without being re-read from the model
- [ ] Anything the backend cannot do is refused by name, not half-done
- [ ] No client data, local paths or secrets in the diff
