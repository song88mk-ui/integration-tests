import sys
from pathlib import Path

# Lets the tests import workspace.py without making routing/ an installed package.
sys.path.insert(0, str(Path(__file__).parent))
