"""Crapper: CRAP score per method from a Cobertura coverage file.

CRAP(m) = c^2 * (1 - cov)^3 + c, where c is the cyclomatic complexity and cov the line coverage of m.
Under 10 is the target. Usage: python -I tools/quality/crapper.py <coverage.cobertura.xml> [threshold]
"""
import sys
import xml.etree.ElementTree as ET


def crap(complexity: float, coverage: float) -> float:
    return complexity ** 2 * (1 - coverage) ** 3 + complexity


def methods(path: str):
    for cls in ET.parse(path).getroot().iter('class'):
        if not cls.get('name', '').startswith('BrainSim.'):
            continue  # generated code (source-generated regexes) is not ours to split
        for m in cls.iter('method'):
            c = float(m.get('complexity', '1'))
            cov = float(m.get('line-rate', '0'))
            yield crap(c, cov), c, cov, cls.get('name'), m.get('name'), cls.get('filename'), m.find('lines/line')


def main() -> None:
    path = sys.argv[1]
    threshold = float(sys.argv[2]) if len(sys.argv) > 2 else 10
    rows = sorted((r for r in methods(path) if r[0] >= threshold), key=lambda r: -r[0])
    for score, c, cov, cls, name, filename, line in rows:
        at = f"{filename}:{line.get('number')}" if line is not None else filename
        print(f"{score:7.1f}  c={c:<4.0f} cov={cov:4.0%}  {cls}.{name}  {at}")
    print(f"{len(rows)} methods at CRAP {threshold:g} or above")


if __name__ == '__main__':
    assert crap(1, 1) == 1 and crap(10, 0) == 110 and crap(4, 0.5) == 6, "CRAP formula"
    main()
