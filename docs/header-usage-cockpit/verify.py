"""Validate the offline Dossier contract and local navigation, without product builds."""
from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import urlsplit, unquote
import json

root = Path(__file__).resolve().parent
class Document(HTMLParser):
    def __init__(self, source):
        super().__init__()
        self.links, self.ids, self.sections, self.decisions = [], set(), set(), set()
        self.feed(source)
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if 'id' in attrs:
            assert attrs['id'] not in self.ids, f"Duplicate id {attrs['id']}"
            self.ids.add(attrs['id'])
        for name in ['href', 'src']:
            if name in attrs:
                self.links.append(attrs[name])
        if 'data-concept-section' in attrs:
            self.sections.add(attrs['data-concept-section'])
        if 'data-decision-id' in attrs:
            self.decisions.add(attrs['data-decision-id'])

files = list(root.glob('*.html'))
for file in files:
    source = file.read_text()
    doc = Document(source)
    assert '\u2014' not in source, f'Em dash in {file}'
    for link in doc.links:
        url = urlsplit(link)
        if url.scheme or url.netloc:
            continue
        target = (file.parent / unquote(url.path)).resolve() if url.path else file
        assert target.is_file(), f'Missing link: {file.name}: {link}'
        if url.fragment and target.suffix == '.html':
            assert unquote(url.fragment) in Document(target.read_text()).ids, f'Missing fragment {link}'
index = Document((root / 'index.html').read_text())
assert index.sections >= {'alternatives','recommendation','evidence','open-decisions'}
assert len(index.decisions) == 5
workbench = json.loads((root / 'workbench.json').read_text())
assert workbench['schemaVersion'] == 1
assert workbench['sourceTaskKeys'] == ['AGT-2913']
assert workbench['status'] == 'decision-pending'
assert workbench['entrypoint'] == 'index.html'
for key in ['id','title','summary','phase','updatedAt']:
    assert workbench[key]
assert len(workbench['implementationTasks']) == 7
for task in workbench['implementationTasks']:
    assert task['title'] and task['promptMarkdown']
    scope = task['acceptanceScope']
    assert scope['deliveryMode'] == 'bounded-slice'
    assert scope['slice'] and len(scope['criteria']) == 4
manifest = json.loads((root / 'assets/capture-manifest.json').read_text())
assert len(manifest['frames']) == 42
assert len(set(manifest['frames'])) == 42
assert len(list((root/'assets').glob('*.png'))) == 42
for frame in manifest['frames']:
    assert (root/'assets'/frame).is_file()
for surface in ['header','board','detail']:
    for width in [390,1024,1728]:
        for theme in ['light','dark']:
            for stage in ['before','after']:
                name = f'{surface}-{stage}-{width}-{theme}--mocked.png'
                assert name in manifest['frames']
                assert name in (root/'index.html').read_text()
canonical = root.parent/'app/templates/article-document-v2.html'
import re
style = re.search(r'<style data-article-template="v2">[\s\S]*?</style>', canonical.read_text()).group()
assert style in (root/'index.html').read_text(), 'House template style must remain verbatim'
print('PASS: schemaVersion 1; 7 bounded slices; 5 settled decisions; all local files/fragments; 42-frame matrix; exact article-v2 style.')
