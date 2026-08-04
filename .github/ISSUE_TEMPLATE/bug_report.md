---
name: Bug report
about: Something behaved differently from what it said it would
labels: bug
---

## What happened

## What you expected

## The output of `project_health`

This says which backend is running, whether Microsoft Project is reachable, and what the capability
matrix allows — most reports are answered by it alone.

```json

```

## The schedule

What format, roughly how many tasks, and anything unusual about it: a six-day working week, several
calendars, no baseline, no logic between tasks, a language other than English in the task names.

Please do not attach a client's schedule. If the problem depends on the file, a description of its
shape is usually enough; if it is not, say so and we will work out how to reproduce it without
sending anything confidential.

## The calls you made

The tool names and arguments, in order.

## Version

Output of `dotnet tool list -g | grep -i horizun`, or the commit you built from.
