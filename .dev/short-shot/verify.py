from pathlib import Path
import hashlib
import os

parts = [Path(f'.dev/short-shot/{i:02}.patch').read_text(encoding='utf-8-sig') for i in range(1, 10)]
patch = ''.join(parts).encode('utf-8')
assert hashlib.sha256(patch).hexdigest() == '19f8d7b9567ab73fc4444cbaa0c0423aa961ec3cba499532fa11b8ddb1864923', 'Source patch transfer mismatch'
path = Path(os.environ['RUNNER_TEMP']) / 'short-shot.patch'
path.write_bytes(patch)
print(path)
