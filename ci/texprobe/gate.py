#!/usr/bin/env python3
"""Pass/fail gate for ci/texprobe.

Reads the run log, prints every [TEXPROBE] line in order, and fails the job when
  * the probe did not print RESULT=OK,
  * a MECHANISM the job exists to establish came back REFUTED (no silent green),
  * any assert=... ok=false.
Exit code 0 only when all three hold.
"""
import re
import sys

MUST_CONFIRM = ["M1", "M2", "M3", "M4"]

line_re = re.compile(r"\[TEXPROBE\]\s+(.*)$")


def main(path):
    lines = []
    try:
        with open(path, 'r', errors='replace') as fh:
            for raw in fh:
                m = line_re.search(raw.rstrip('\n'))
                if m:
                    lines.append(m.group(1))
    except FileNotFoundError:
        print(f'gate: log missing: {path}')
        return 2

    for line in lines:
        print(line)

    mechanisms = {}
    for line in lines:
        m = re.match(r'MECHANISM=(\S+)\s+name=(\S+)\s+verdict=(\S+)', line)
        if m:
            mechanisms[m.group(1)] = (m.group(2), m.group(3))

    failed = [line for line in lines if re.search(r'assert=\S+ ok=false\b', line)]
    refuted = [k for k in MUST_CONFIRM if mechanisms.get(k, ('', 'MISSING'))[1] != 'CONFIRMED']
    result_ok = any(line.startswith('RESULT=OK') for line in lines)

    print('\n--- gate ---')
    print(f'result_ok={result_ok} mechanisms={len(mechanisms)} failed_asserts={len(failed)}')
    for k in sorted(mechanisms):
        print(f'  {k}: {mechanisms[k][0]} -> {mechanisms[k][1]}')
    for line in failed:
        print('  FAILED ' + line)
    for k in refuted:
        print(f'  NOT CONFIRMED: {k} ({mechanisms.get(k, ("missing", "MISSING"))[0]})')

    if result_ok and not failed and not refuted:
        print('gate: PASS')
        return 0
    print('gate: FAIL')
    return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else 'out/texprobe.log'))
