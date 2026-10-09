#!/usr/bin/env python3
"""Fail closed when dotnet test returns zero without exercising its selected tests."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main():
    if len(sys.argv) != 2:
        raise ValueError('Expected one TRX results file.')
    root = ET.parse(Path(sys.argv[1])).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counters = root.find('t:ResultSummary/t:Counters', ns)
    results = root.findall('t:Results/t:UnitTestResult', ns)
    if counters is None or not results:
        raise ValueError('No selected tests executed; check the test filter and build output.')
    total = int(counters.attrib.get('total', '0'))
    executed = int(counters.attrib.get('executed', '0'))
    passed = int(counters.attrib.get('passed', '0'))
    if total != len(results) or executed != total or passed != total:
        raise ValueError(f'Test selection was not fully exercised: total={total}, executed={executed}, passed={passed}. Skips are not acceptance passes.')
    if any(result.attrib.get('outcome') != 'Passed' for result in results):
        raise ValueError('At least one selected test did not pass.')
    print(f'Executed-test verification: {passed}/{total} passed, no skipped or missing results.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, ET.ParseError) as error:
        print(f'Test evidence rejected: {error}', file=sys.stderr)
        sys.exit(1)
