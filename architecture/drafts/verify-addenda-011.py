"""Proposed contract fixture checks, not a runtime loader. No writes to service repos."""
from pathlib import Path
from copy import deepcopy
import hashlib
import json
import re
import sys
from jsonschema import Draft202012Validator, FormatChecker
LEGACY = '--allow-legacy-container-roots' in sys.argv  # ADDENDA-011.1: default OFF (CI intent)

BASE = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((BASE / 'architecture-layer.schema.json').read_text())
Draft202012Validator.check_schema(SCHEMA)
STRICT_VALIDATOR = Draft202012Validator(SCHEMA, format_checker=FormatChecker())
_legacy = deepcopy(SCHEMA)
_legacy['$defs']['layer']['properties']['flowRef']['required'] = ['containerId']
_legacy['$defs']['layer']['allOf'][0]['then'] = {'required': ['flowRef']}
LEGACY_VALIDATOR = Draft202012Validator(_legacy, format_checker=FormatChecker())
VALIDATOR = STRICT_VALIDATOR
REG = json.loads((BASE / 'registrations/customerproject.dev.json').read_text())
CAT = json.loads((BASE / 'layers/customerproject/index.json').read_text())
LAYERS = [json.loads((BASE / 'layers/customerproject' / e['path']).read_text()) for e in CAT['layers']]

# V-012 (guide-mapped price calculator layers, 2026-11-11): every node states its evidence status vs code.
GUIDE_PREFIX = 'layer:price.api.get-prices.calculator.'
GUIDE_STATUS = re.compile(r'^Guide vs code: (confirmed|design-only|conflicting|code-only)\. \S')
def check_guide_status(layer, require):
    if not layer['id'].startswith(GUIDE_PREFIX): return
    for node in layer['nodes']:
        m = GUIDE_STATUS.match(node['responsibility'])
        require(m, 'V-012 missing/unknown evidence status ' + node['id'])
        if m.group(1) != 'confirmed':
            require(node['uncertainties'], 'V-012 non-confirmed status without uncertainty ' + node['id'])
        if m.group(1) in ('design-only', 'conflicting'):
            require(any(x['kind'] == 'confluence' for x in node['sources']), 'V-012 design claim without Confluence source ' + node['id'])

def check(cat, layers, reg=REG, legacy=False):
    VALIDATOR = LEGACY_VALIDATOR if legacy else STRICT_VALIDATOR
    VALIDATOR.validate(cat)
    by = {}
    seen_nodes, seen_edges, paths = set(), set(), set()
    def require(ok, code):
        if not ok: raise ValueError(code)
    require(cat['registrationId'] == reg['id'] and cat['project'] == reg['project'], 'V-002 catalog binding')
    for entry in cat['layers']:
        require(entry['path'] not in paths, 'V-002 duplicate path')
        paths.add(entry['path'])
        root = (BASE / 'layers' / cat['project']).resolve()
        require((root / entry['path']).resolve().is_relative_to(root), 'V-003 unsafe path')
    require(len(layers) == len(cat['layers']), 'V-002 catalog count')
    for layer, entry in zip(layers, cat['layers']):
        VALIDATOR.validate(layer)
        require(layer['id'] == entry['id'] and layer['id'] not in by, 'V-002 duplicate/mismatched layer')
        require(layer['project'] == cat['project'] and layer['id'].startswith('layer:' + cat['project'] + '.'), 'V-002 project')
        by[layer['id']] = layer
        check_guide_status(layer, require)
        ids = set()
        for node in layer['nodes']:
            require(node['id'].startswith('comp:' + cat['project'] + '.') and node['id'] not in seen_nodes, 'V-002 node')
            ids.add(node['id']); seen_nodes.add(node['id'])
            if 'flowEntityId' in node:
                require(node['flowEntityId'] in {x['id'] for x in reg['entities']}, 'V-006 entity')
        for edge in layer['edges']:
            require(edge['id'].startswith('aedge:' + cat['project'] + '.') and edge['id'] not in seen_edges, 'V-002 edge')
            seen_edges.add(edge['id'])
            require(edge['from'] in ids and edge['to'] in ids and edge['from'] != edge['to'], 'V-007 endpoint')
        # V-008 (Flow v2 invariant): no boundary queue/topic both receives from and sends to the same component.
        kinds = {x['id']: x for x in layer['nodes']}
        for nid, node in kinds.items():
            if node['boundary'] != 'outside': continue
            ins = {e['from'] for e in layer['edges'] if e['to'] == nid}
            outs = {e['to'] for e in layer['edges'] if e['from'] == nid}
            require(not (ins & outs), 'V-008 bidirectional queue ' + nid)
    opens, roots = {}, set()
    scopes = {'local': {'local'}, 'cloud': {'cloud'}, 'both': {'local', 'cloud'}}
    containers = {x['id']: x for x in reg['containers']}
    handlers = {x['id']: x for x in reg['handlers']}
    for layer in layers:
        chain, cur = set(), layer
        while cur['parentLayerId'] is not None:
            require(cur['id'] not in chain, 'V-004 cycle')
            chain.add(cur['id'])
            require(cur['parentLayerId'] in by, 'V-004 dangling parent')
            parent = by[cur['parentLayerId']]
            require(scopes[cur['scope']] <= scopes[parent['scope']], 'V-002 widened scope')
            cur = parent
        if layer['parentLayerId'] is None:
            ref = layer['flowRef']; c = ref['containerId']; h = ref.get('handlerId')
            require(c in containers, 'V-006 container')
            require(legacy or h, 'V-011 root lacks handlerId (container-only roots are not Explore targets)')
            require(not h or h in handlers and handlers[h]['containerId'] == c, 'V-011 handler dangling or wrong container')
            key = h or ('container', c)
            require(key not in roots, 'V-011 two roots claim same handler/source')
            roots.add(key)
            require(scopes[layer['scope']] <= scopes[(handlers[h] if h else containers[c]).get('scope', containers[c].get('scope', 'both'))], 'V-002 root scope')
        for node in layer['nodes']:
            if 'opensLayerId' not in node: continue
            target = node['opensLayerId']
            require(target in by and by[target]['parentLayerId'] == layer['id'], 'V-005 dangling/non-child opener')
            require(node['kind'] == 'component' and node['boundary'] == 'inside', 'V-005 invalid opener')
            opens[target] = opens.get(target, 0) + 1
    for layer in layers:
        require(opens.get(layer['id'], 0) == (0 if layer['parentLayerId'] is None else 1), 'V-005 orphan/duplicate opener')
    return True

LEGACY_ROOTS = []
FAILS = []  # strict mode reports every project, then exits non-zero
def real(cat, ls, reg):
    missing = [l['id'] for l in ls if l['parentLayerId'] is None and 'handlerId' not in l.get('flowRef', {})]
    if missing and not LEGACY:
        return FAILS.append('FAIL (CONTRACT-PENDING) V-011 roots lack handlerId in ' + cat['project'] + ': ' + ', '.join(missing) + ' -- see drafts/PENDING-per-item-roots.md')
    try:
        return check(cat, ls, reg)
    except Exception as exc:
        if not LEGACY: return FAILS.append('FAIL (CONTRACT-PENDING) ' + cat['project'] + ': ' + getattr(exc, 'message', str(exc))[:200] + ' -- see drafts/PENDING-per-item-roots.md')
        LEGACY_ROOTS.extend(l['id'] for l in ls if l['parentLayerId'] is None and 'handlerId' not in l['flowRef'])
        return check(cat, ls, reg, legacy=True)
print('STRICT customerproject:', 'PASS' if real(CAT, LAYERS, REG) else 'FAIL') if not LEGACY else real(CAT, LAYERS, REG)
PREG = json.loads((BASE / 'registrations/price.dev.json').read_text())
PCAT = json.loads((BASE / 'layers/price/index.json').read_text())
PLAYERS = [json.loads((BASE / 'layers/price' / e['path']).read_text()) for e in PCAT['layers']]
PREG = json.loads((BASE / 'registrations/price.dev.json').read_text())
print('STRICT price:', 'PASS' if real(PCAT, PLAYERS, PREG) else 'FAIL') if not LEGACY else real(PCAT, PLAYERS, PREG)
def forest_depth(ls):
    by = {l['id']: l for l in ls}; best = 0
    for l in ls:
        d, cur = 1, l
        while cur['parentLayerId']: cur = by[cur['parentLayerId']]; d += 1
        best = max(best, d)
    return best
assert forest_depth(LAYERS) >= 2 and forest_depth(PLAYERS) >= 2, 'real multi-depth required'
# A valid three-layer synthetic forest exercises arbitrary-depth navigation (built from the ingress worked example only).
_B = next(i for i, l in enumerate(LAYERS) if l['id'] == 'layer:customerproject.fn.ingress')  # ADDENDA-011.1 base
cat, layers = deepcopy(CAT), [deepcopy(LAYERS[_B])]
layers[0]['flowRef'] = {'containerId': 'container:customerproject.ingress-app', 'handlerId': 'customerproject.fn.ingress'}
cat['layers'] = [cat['layers'][_B]]
for depth in (2, 3):
    child = deepcopy(LAYERS[_B]); old_ids = {n['id']: n['id'] + '.test' + str(depth) for n in child['nodes']}
    child['id'] += '.test' + str(depth)
    child['parentLayerId'] = layers[-1]['id']; child.pop('flowRef')
    for n in child['nodes']: n['id'] = old_ids[n['id']]
    for e in child['edges']:
        e['id'] += '.test' + str(depth); e['from'] = old_ids[e['from']]; e['to'] = old_ids[e['to']]
    layers[-1]['nodes'][1]['opensLayerId'] = child['id']
    cat['layers'].append({'id': child['id'], 'path': 'test' + str(depth) + '.json'}); layers.append(child)
check(cat, layers)

cases = {}
def bad(name, mutate, c=cat, ls=layers, reg=REG):
    cc, ll = deepcopy(c), deepcopy(ls); mutate(cc, ll)
    try: check(cc, ll, reg)
    except Exception as exc:
        # Every rejected fixture must genuinely fail, regardless of schema vs semantic stage.
        cases[name] = type(exc).__name__; return
    raise AssertionError('Fixture unexpectedly accepted: ' + name)
bad('dangling parent', lambda c,l: l[1].update(parentLayerId='layer:customerproject.missing'))
bad('parent cycle', lambda c,l: l[1].update(parentLayerId=l[2]['id']))
bad('dangling opens', lambda c,l: l[0]['nodes'][1].update(opensLayerId='layer:customerproject.missing'))
bad('orphan child', lambda c,l: l[0]['nodes'][1].pop('opensLayerId'))
bad('duplicate opener', lambda c,l: l[0]['nodes'][3].update(opensLayerId=l[1]['id']))
bad('non-child link', lambda c,l: l[0]['nodes'][1].update(opensLayerId=l[2]['id']))
bad('outside opener', lambda c,l: l[0]['nodes'][1].update(boundary='outside'))
bad('missing endpoint', lambda c,l: l[0]['edges'][0].update(to='comp:customerproject.missing'))
bad('duplicate node', lambda c,l: l[0]['nodes'].append(deepcopy(l[0]['nodes'][0])))
bad('cross-project layer', lambda c,l: l[0].update(project='price'))
bad('root without handlerId', lambda c,l: l[0]['flowRef'].pop('handlerId'))
bad('handler wrong container', lambda c,l: l[0]['flowRef'].update(handlerId='customerproject.fn.domain'))
bad('dangling handler', lambda c,l: l[0]['flowRef'].update(handlerId='customerproject.fn.missing'))
def _dup(c,l):
    x = deepcopy(l[0]); x['id'] = 'layer:customerproject.dup'; x['nodes'] = [dict(n, id=n['id']+'.dup') for n in x['nodes']]
    for n in x['nodes']: n.pop('opensLayerId', None)
    m = {o['id']: o['id']+'.dup' for o in l[0]['nodes']}
    x['edges'] = [dict(e, id=e['id']+'.dup', **{'from': m[e['from']], 'to': m[e['to']]}) for e in x['edges']]
    l.append(x); c['layers'].append({'id': x['id'], 'path': 'dup.json'})
bad('two roots same handler', _dup)
bad('flow container missing', lambda c,l: l[0]['flowRef'].update(containerId='container:customerproject.missing'))
bad('flow entity missing', lambda c,l: l[0]['nodes'][0].update(flowEntityId='q:missing'))
bad('path traversal', lambda c,l: c['layers'][0].update(path='../secret.json'))
bad('absolute path', lambda c,l: c['layers'][0].update(path='/etc/passwd'))
bad('duplicate catalog path', lambda c,l: c['layers'][1].update(path=c['layers'][0]['path']))
bad('live edge kind', lambda c,l: l[0]['edges'][0].update(kind='observed'))
bad('empty evidence', lambda c,l: l[0]['nodes'][0].update(sources=[]))
bad('bidirectional queue', lambda c,l: l[0]['edges'].append({**deepcopy(l[0]['edges'][0]), 'id': 'aedge:customerproject.ingress.back', 'from': l[0]['edges'][0]['to'], 'to': l[0]['edges'][0]['from']}))
bad('inference without rationale', lambda c,l: l[0]['sources'].append({'kind':'inferred','ref':'dsp-customerproject/README.md','confidence':'inferred'}))

# Depth 4/5 fixtures on the REAL price forest (guide-mapped calculator layers, 2026-11-11).
check(PCAT, PLAYERS, PREG)
_PI = {l['id']: i for i, l in enumerate(PLAYERS)}
def _pl(l, lid): return l[_PI['layer:price.' + lid]]
def _pn(l, lid, nid): return next(n for n in _pl(l, lid)['nodes'] if n['id'] == 'comp:price.' + nid)
def pbad(name, mutate): bad(name, mutate, PCAT, PLAYERS, PREG)
pbad('d4 dangling opensLayerId', lambda c,l: _pn(l, 'api.get-prices.calculator', 'calc.context').update(opensLayerId='layer:price.api.get-prices.calculator.missing'))
def _cyc(c, l):  # detach the d4 opener too, so the rejection comes from the V-004 cycle walk, not V-005
    _pn(l, 'api.get-prices.calculator', 'calc.discount').pop('opensLayerId')
    _pl(l, 'api.get-prices.calculator.discount').update(parentLayerId='layer:price.api.get-prices.calculator.discount.relations')
pbad('d4/d5 parent cycle', _cyc)
pbad('d5 orphan child (opener removed)', lambda c,l: _pn(l, 'api.get-prices.calculator.discount', 'calc-disc.relation').pop('opensLayerId'))
pbad('d5 outside opener', lambda c,l: _pn(l, 'api.get-prices.calculator.discount', 'calc-disc.relation').update(boundary='outside'))
pbad('d5 opener points at d4 sibling', lambda c,l: _pn(l, 'api.get-prices.calculator.discount', 'calc-disc.relation').update(opensLayerId='layer:price.api.get-prices.calculator.select'))
pbad('d4 missing evidence status', lambda c,l: _pn(l, 'api.get-prices.calculator.context', 'calc-ctx.prms').update(responsibility='Walk OOTYPE.PRMS.'))
pbad('d4 unknown evidence status', lambda c,l: _pn(l, 'api.get-prices.calculator.context', 'calc-ctx.prms').update(responsibility='Guide vs code: probably. Walk OOTYPE.PRMS.'))
pbad('d4 conflicting without uncertainty', lambda c,l: _pn(l, 'api.get-prices.calculator.context', 'calc-ctx.gate').update(uncertainties=[]))
pbad('d4 design-only without Confluence source', lambda c,l: _pn(l, 'api.get-prices.calculator.context', 'calc-ctx.prms').update(sources=[x for x in _pn(l, 'api.get-prices.calculator.context', 'calc-ctx.prms')['sources'] if x['kind'] != 'confluence']))
pbad('d5 cross-layer edge endpoint', lambda c,l: _pl(l, 'api.get-prices.calculator.discount.relations')['edges'][0].update(to='comp:price.calc-disc.net'))

# Validate existing registrations with their unchanged schema.
rs = Draft202012Validator(json.loads((BASE / 'registration.schema.json').read_text()), format_checker=FormatChecker())
for filename in ('customerproject.dev.json', 'price.dev.json'):
    rs.validate(json.loads((BASE / 'registrations' / filename).read_text()))
# Reviewed citations and stored per-file revisions resolve without network access.
count = 0; FALLBACK = 0
# Repo copies: canonical /workspace/dsp/<ref>; if a whole repo copy has been moved away, an alternate local copy is
# accepted ONLY when the stored sha256 still matches byte-for-byte (content-addressed evidence).
ALT_ROOTS = [Path('/workspace/dsp-kesko')]
def resolve(ref):
    global FALLBACK
    p = Path('/workspace/dsp') / ref
    if p.is_file(): return p
    for r in ALT_ROOTS:
        if (r / ref).is_file(): FALLBACK += 1; return r / ref
    return p
for item in [x for L_ in LAYERS + PLAYERS for x in [L_, *L_['nodes'], *L_['edges']]]:
    for source in item['sources']:
        if source['kind'] in ('repo', 'registration'):
            path = resolve(source['ref'])
            assert path.is_file(), path
            assert source['revision'] == 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest(), path
        elif source['kind'] == 'confluence':
            assert source['ref'].rsplit('/',1)[1] in {'22779513','22779013','22774292','22777574','22773861','22774388','22774149','22776628','22774078','22774769','22776164'}
        count += 1
print('MODE', 'LEGACY (CONTRACT-PENDING roots: ' + ', '.join(LEGACY_ROOTS) + ')' if LEGACY else 'STRICT')
print('PASS schema (layer/catalog), unchanged customerproject+price registrations')
print('PASS real forests: customerproject', len(LAYERS), 'layers depth', forest_depth(LAYERS), '| price', len(PLAYERS), 'layers depth', forest_depth(PLAYERS), '| synthetic depth 3')
print('PASS V-008 no bidirectional boundary node in any layer')
print('PASS', len(cases), 'negative fixtures:', ', '.join(cases))
for f_ in FAILS: print(f_)
print('PASS', count, 'citation occurrences: local file/hash checks + 11 actually opened Confluence page IDs (3 customerproject + 8 price)' + (' | ' + str(FALLBACK) + ' repo citations hash-matched in alternate copy ' + ', '.join(map(str, ALT_ROOTS)) + ' (canonical copy absent)' if FALLBACK else ''))
_gl = [l for l in PLAYERS if l['id'].startswith(GUIDE_PREFIX)]
_st = {}
for n in (n for l in _gl for n in l['nodes']): k = GUIDE_STATUS.match(n['responsibility']).group(1); _st[k] = _st.get(k, 0) + 1
print('PASS V-012 guide-mapped layers:', len(_gl), '| node evidence status', dict(sorted(_st.items())))
if FAILS: raise SystemExit('STRICT RESULT: FAIL (' + str(len(FAILS)) + ' project(s) CONTRACT-PENDING)')
print('STRICT RESULT: PASS (all projects)' if not LEGACY else 'LEGACY RESULT: PASS')
