"""Display preference persistence, heading controls and grid sizing in real Vditor."""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('image-browser-checks.py').read_text().split('for theme in')[0])
def display():
 c.eval("document.querySelector('.vditor-toolbar [data-type=display]').click()")
 wait("!!document.getElementById('display-dialog')")
def set_fields(height,gap):
 c.eval(f"(()=>{{let f=document.querySelector('#display-dialog form');f.elements.lineHeight.value={height};f.elements.paragraphGap.value={gap};f.dispatchEvent(new Event('input',{{bubbles:true}}));}})()")
def metrics():
 return c.eval("(()=>{let p=document.querySelector('.vditor-ir p'),s=getComputedStyle(p);return [parseFloat(s.lineHeight)/parseFloat(s.fontSize),parseFloat(s.marginBottom)]})()")
assert c.eval("parseFloat(getComputedStyle(document.querySelector('[data-type=display]')).fontSize)")>=12
assert c.eval("(()=>{let t=document.querySelector('.vditor-toolbar').getBoundingClientRect(),b=document.querySelector('[data-type=display]').getBoundingClientRect();return t.right-b.right<40})()")
assert metrics()==[1.5,4],metrics()
display();set_fields(1.25,2);assert metrics()==[1.25,2],metrics()
c.eval("document.querySelector('#display-dialog [data-cancel]').click()")
wait("!document.getElementById('display-dialog')");assert metrics()==[1.5,4]
display();set_fields(1.35,6)
c.eval("document.querySelector('#display-dialog button[value=save]').click()")
wait("!document.getElementById('display-dialog')")
assert json.loads((root/'.state.json').read_text())['display']=={'lineHeight':1.35,'paragraphGap':6}
c.call('Page.reload');wait("!!document.querySelector('.vditor-ir p')")
assert abs(metrics()[0]-1.35)<.001 and metrics()[1]==6,metrics()
c.eval("document.getElementById('source-mode').click()")
assert abs(c.eval("(()=>{let s=getComputedStyle(document.querySelector('.vditor-sv'));return parseFloat(s.lineHeight)/parseFloat(s.fontSize)})()")-1.35)<.001
c.eval("document.getElementById('preview-mode').click()")
display();c.eval("document.querySelector('#display-dialog [data-reset]').click()")
assert metrics()==[1.5,4]
c.eval("document.querySelector('#display-dialog button[value=save]').click()")
wait("!document.getElementById('display-dialog')")
print('PASS display preview, cancel, save, reload persistence, both modes and reset')
for typ,tag in [('headings','h1'),('heading2','h2'),('heading3','h3')]:
 c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.focus();s.select()")
 c.call('Input.insertText',{'text':'before\n\nHeading target\n\nafter\n'})
 c.eval("document.getElementById('preview-mode').click()")
 time.sleep(.15)
 c.eval("(()=>{let p=[...document.querySelectorAll('.vditor-ir p')].find(x=>x.textContent==='Heading target'),r=document.createRange();r.selectNodeContents(p);r.collapse(true);getSelection().removeAllRanges();getSelection().addRange(r)})()")
 c.eval(f"document.querySelector('.vditor-toolbar [data-type={typ}]').click()")
 wait(f"[...document.querySelectorAll('.vditor-ir {tag}')].some(x=>x.textContent.includes('Heading target'))")
 c.eval('window.notebookFlush()')
 assert ('#'*int(tag[-1])+' Heading target') in (folder/'main.md').read_text()
 assert 'before' in (folder/'main.md').read_text() and 'after' in (folder/'main.md').read_text()
print('PASS H1/H2/H3 apply directly and serialize as Markdown without changing adjacent paragraphs')
# Check the original heading shortcut and the absence of shortcuts on added buttons.
assert 'Ctrl+H' in c.eval("document.querySelector('[data-type=headings]').getAttribute('aria-label')")
for typ in ('heading2','heading3'):
 assert 'Ctrl' not in c.eval(f"document.querySelector('[data-type={typ}]').getAttribute('aria-label')")
assert c.eval("document.querySelector('[data-type=check]').getAttribute('aria-label').includes('任务/多选框')")
# Hover 5x5 then click, rather than requiring a second confirmation.
c.eval("document.querySelector('.vditor-toolbar [data-type=table]').click()")
wait("!!document.getElementById('table-picker')")
c.eval("document.querySelector('#table-picker [data-row=\"5\"][data-column=\"5\"]').dispatchEvent(new PointerEvent('pointerenter'))")
assert c.eval("document.querySelectorAll('#table-picker .selected').length")==25
assert c.eval("document.querySelector('#table-picker output').textContent")=='5 × 5'
c.screenshot('/tmp/notebook-new-toolbar-light.png')
c.eval("document.querySelector('#table-picker [data-row=\"5\"][data-column=\"5\"]').click()")
wait("document.querySelector('.vditor-ir table')?.rows.length===5")
assert c.eval("document.querySelector('.vditor-ir table').rows[0].cells.length")==5
c.eval("document.querySelector('.vditor-toolbar [data-type=table]').click()")
wait("!!document.getElementById('table-picker')")
c.eval("[...document.querySelectorAll('#table-picker button')].find(x=>x.textContent==='自定义…').click();var f=document.querySelector('#table-picker form');f.elements.rows.value=12;f.elements.columns.value=2;f.requestSubmit()")
wait("document.querySelectorAll('.vditor-ir table').length===2")
assert c.eval("[...document.querySelectorAll('.vditor-ir table')].map(t=>[t.rows.length,t.rows[0].cells.length])")==[[5,5],[12,2]]
c.eval("document.getElementById('theme').click()")
wait("document.documentElement.dataset.theme==='dark'")
c.eval("document.querySelector('.vditor-toolbar [data-type=table]').click()")
wait("!!document.getElementById('table-picker')")
c.screenshot('/tmp/notebook-new-toolbar-dark.png');key('Escape',27)
print('PASS original heading shortcut label, task naming, 5x5 hover/click and custom 12x2 table creation')
