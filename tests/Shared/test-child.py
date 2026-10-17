#!/usr/bin/env python3
"""A test double for a child process whose exact output bytes a test dictates.

Writes the stdout and stderr bytes its data file names, then exits with the code it names. The data
file is `<name>.json` beside the path this script was invoked as (sys.argv[0], not resolved), so a
test reaches it through a per-test symlink and writes only that non-executable file: nothing
executable is written at run time (D47, the same class as D44).

The data file holds:
    {"stdout": <hex>, "stderr": <hex>, "exit": <int>}
"""

import json
import os
import sys

invoked = os.path.abspath(sys.argv[0])
with open(invoked + ".json", encoding="ascii") as f:
    spec = json.load(f)

sys.stdout.buffer.write(bytes.fromhex(spec["stdout"]))
sys.stdout.buffer.flush()
sys.stderr.buffer.write(bytes.fromhex(spec["stderr"]))
sys.stderr.buffer.flush()
sys.exit(spec["exit"])
