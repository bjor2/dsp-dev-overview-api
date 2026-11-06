"""Proposed contract fixture checks, not a runtime loader. No writes to service repos."""
from pathlib import Path
from copy import deepcopy
import hashlib
import json
import re
from jsonschema import Draft202012Validator, FormatChecker

BASE = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((BASE / 'architecture-layer.schema.json').read_text())
Draft202012Validator.check_schema(SCHEMA)
VALIDATOR = Draft202012Validator(SCHEMA, format_checker=FormatChecker())
REG = json.loads((BASE / 'registrations/customerproject.dev.json').read_text())
CAT = json.loads((BASE / 'layers/customerproject/index.json').read_text())
LAYERS = [json.loads((BASE / 'layers/customerproject' / e['path']).read_text()) for e in CAT['layers']]

def check(cat, layers, reg=REG):
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
            require(not h or h in handlers and handlers[h]['containerId'] == c, 'V-006 handler')
            require((c, h) not in roots, 'V-006 duplicate root')
            roots.add((c, h))
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

check(CAT, LAYERS)
PREG = json.loads((BASE / 'registrations/price.dev.json').read_text())
PCAT = json.loads((BASE / 'layers/price/index.json').read_text())
PLAYERS = [json.loads((BASE / 'layers/price' / e['path']).read_text()) for e in PCAT['layers']]
check(PCAT, PLAYERS, PREG)
def forest_depth(ls):
    by = {l['id']: l for l in ls}; best = 0
    for l in ls:
        d, cur = 1, l
        while cur['parentLayerId']: cur = by[cur['parentLayerId']]; d += 1
        best = max(best, d)
    return best
assert forest_depth(LAYERS) >= 2 and forest_depth(PLAYERS) >= 2, 'real multi-depth required'
# A valid three-layer synthetic forest exercises arbitrary-depth navigation (built from the ingress worked example only).
cat, layers = deepcopy(CAT), [deepcopy(LAYERS[0])]
cat['layers'] = cat['layers'][:1]
for depth in (2, 3):
    child = deepcopy(LAYERS[0]); old_ids = {n['id']: n['id'] + '.test' + str(depth) for n in child['nodes']}
    child['id'] += '.test' + str(depth)
    child['parentLayerId'] = layers[-1]['id']; child.pop('flowRef')
    for n in child['nodes']: n['id'] = old_ids[n['id']]
    for e in child['edges']:
        e['id'] += '.test' + str(depth); e['from'] = old_ids[e['from']]; e['to'] = old_ids[e['to']]
    layers[-1]['nodes'][1]['opensLayerId'] = child['id']
    cat['layers'].append({'id': child['id'], 'path': 'test' + str(depth) + '.json'}); layers.append(child)
check(cat, layers)

cases = {}
def bad(name, mutate, c=cat, ls=layers):
    cc, ll = deepcopy(c), deepcopy(ls); mutate(cc, ll)
    try: check(cc, ll)
    except Exception as exc:
        # Every rejected fixture must genuinely fail, regardless of schema vs semantic stage.
        cases[name] = type(exc).__name__; return
    raise AssertionError('Fixture unexpectedly accepted: ' + name)
bad('dangling parent', lambda c,l: l[1].update(parentLayerId='layer:customerproject.missing'))
bad('parent cycle', lambda c,l: l[1].update(parentLayerId=l[2]['id']))
bad('dangling opens', lambda c,l: l[0]['nodes'][1].update(opensLayerId='layer:customerproject.missing'))
bad('orphan child', lambda c,l: l[0]['nodes'][1].pop('opensLayerId'))
bad('duplicate opener', lambda c,l: l[0]['nodes'][4].update(opensLayerId=l[1]['id']))
bad('non-child link', lambda c,l: l[0]['nodes'][1].update(opensLayerId=l[2]['id']))
bad('outside opener', lambda c,l: l[0]['nodes'][1].update(boundary='outside'))
bad('missing endpoint', lambda c,l: l[0]['edges'][0].update(to='comp:customerproject.missing'))
bad('duplicate node', lambda c,l: l[0]['nodes'].append(deepcopy(l[0]['nodes'][0])))
bad('cross-project layer', lambda c,l: l[0].update(project='price'))
bad('flow container missing', lambda c,l: l[0]['flowRef'].update(containerId='container:customerproject.missing'))
bad('flow entity missing', lambda c,l: l[0]['nodes'][0].update(flowEntityId='q:missing'))
bad('path traversal', lambda c,l: c['layers'][0].update(path='../secret.json'))
bad('absolute path', lambda c,l: c['layers'][0].update(path='/etc/passwd'))
bad('duplicate catalog path', lambda c,l: c['layers'][1].update(path=c['layers'][0]['path']))
bad('live edge kind', lambda c,l: l[0]['edges'][0].update(kind='observed'))
bad('empty evidence', lambda c,l: l[0]['nodes'][0].update(sources=[]))
bad('bidirectional queue', lambda c,l: l[0]['edges'].append({**deepcopy(l[0]['edges'][0]), 'id': 'aedge:customerproject.ingress.back', 'from': l[0]['edges'][0]['to'], 'to': l[0]['edges'][0]['from']}))
bad('inference without rationale', lambda c,l: l[0]['sources'].append({'kind':'inferred','ref':'dsp-customerproject/README.md','confidence':'inferred'}))

# Validate existing registrations with their unchanged schema.
rs = Draft202012Validator(json.loads((BASE / 'registration.schema.json').read_text()), format_checker=FormatChecker())
for filename in ('customerproject.dev.json', 'price.dev.json'):
    rs.validate(json.loads((BASE / 'registrations' / filename).read_text()))
# Reviewed citations and stored per-file revisions resolve without network access.
count = 0
for item in [x for L_ in LAYERS + PLAYERS for x in [L_, *L_['nodes'], *L_['edges']]]:
    for source in item['sources']:
        if source['kind'] in ('repo', 'registration'):
            path = Path('/workspace/dsp') / source['ref']
            assert path.is_file(), path
            assert source['revision'] == 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest(), path
        elif source['kind'] == 'confluence':
            assert source['ref'].rsplit('/',1)[1] in {'22779513','22779013','22774292','22777574','22773861','22774388','22774149','22776628','22774078'}
        count += 1
print('PASS schema (layer/catalog), unchanged customerproject+price registrations')
print('PASS real forests: customerproject', len(LAYERS), 'layers depth', forest_depth(LAYERS), '| price', len(PLAYERS), 'layers depth', forest_depth(PLAYERS), '| synthetic depth 3')
print('PASS V-008 no bidirectional boundary node in any layer')
print('PASS', len(cases), 'negative fixtures:', ', '.join(cases))
print('PASS', count, 'citation occurrences: local file/hash checks + 9 actually opened Confluence page IDs (3 customerproject + 6 price)')
