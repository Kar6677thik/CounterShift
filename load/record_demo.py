"""Records load/demo.sh into an asciicast v2 file, which svg-term-cli renders as the README's SVG.

    python -I load/record_demo.py docs/images/demo.cast
    npx --yes svg-term-cli --in docs/images/demo.cast --out docs/images/demo.svg --window --width 110 --height 34

Long pauses (config reloads, container stop/start) are capped so the animation keeps moving.
The output itself is captured as-is, so every number in the recording is a real response.
"""

import json
import subprocess
import sys
import time

MAX_GAP = 1.2       # seconds; longer waits are compressed
TYPE_DELAY = 0.025  # per character, for the "$ command" lines


def main(out_path):
    events, clock, last = [], 0.0, time.monotonic()
    process = subprocess.Popen(["bash", "load/demo.sh"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, bufsize=0)
    buffer = b""
    while True:
        chunk = process.stdout.read1(4096) if hasattr(process.stdout, "read1") else process.stdout.read(1)
        if not chunk:
            break
        now = time.monotonic()
        clock += min(now - last, MAX_GAP)
        last = now
        buffer += chunk
        *lines, buffer = buffer.split(b"\n")
        for raw in lines:
            text = raw.decode("utf-8", "replace") + "\r\n"
            if "\x1b[1;32m$\x1b[0m" in text:   # a command: type it out
                prompt, command = text.split("\x1b[1;32m$\x1b[0m", 1)
                events.append([round(clock, 3), "o", prompt + "\x1b[1;32m$\x1b[0m"])
                for ch in command:
                    clock += TYPE_DELAY
                    events.append([round(clock, 3), "o", ch])
            else:
                events.append([round(clock, 3), "o", text])
    process.wait()
    clock += 3
    events.append([round(clock, 3), "o", ""])
    header = {"version": 2, "width": 110, "height": 34, "title": "CounterShift: a live storage migration, with a rollback"}
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(header) + "\n")
        for event in events:
            f.write(json.dumps(event) + "\n")
    print(f"recorded {len(events)} events, {clock:.1f} s -> {out_path}")


if __name__ == "__main__":
    main(sys.argv[1])
