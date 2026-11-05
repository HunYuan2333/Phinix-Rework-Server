"""Fail-closed coverage report validation. Never substitutes test pass for a report."""
import xml.etree.ElementTree as ET


def summarize(path, assemblies):
    root = ET.parse(path).getroot()
    packages = root.findall('./packages/package')
    names = {p.attrib['name'] for p in packages}
    if len(names) != len(packages) or names != set(assemblies):
        raise ValueError(f'Coverage assembly scope mismatch: {sorted(names)}')
    counts = {name: int(root.attrib[name]) for name in
              ['lines-valid', 'lines-covered', 'branches-valid', 'branches-covered']}
    for kind in ['lines', 'branches']:
        if counts[kind + '-valid'] <= 0 or not 0 <= counts[kind + '-covered'] <= counts[kind + '-valid']:
            raise ValueError('Missing or invalid coverage denominator')
    if counts['lines-covered'] == 0:
        raise ValueError('No instrumented product lines were executed')
    files = sorted({cls.attrib['filename'] for p in packages for cls in p.findall('./classes/class')})
    if not files or any('/Dependencies/' in f or '/obj/' in f or '/compiled/' in f for f in files):
        raise ValueError('Unexpected generated/vendor/empty source scope')
    return {'assemblies': sorted(names), 'sourceFiles': files, **counts,
            'linePercent': round(100 * counts['lines-covered'] / counts['lines-valid'], 3),
            'branchPercent': round(100 * counts['branches-covered'] / counts['branches-valid'], 3)}
