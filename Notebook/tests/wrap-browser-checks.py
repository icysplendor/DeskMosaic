"""Run after PWA browser/layout checks, using the same shared desktop editor."""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Network.enable');c.call('Network.setBypassServiceWorker',{'bypass':True})
c.eval("localStorage.setItem('test-offline','true')");c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"Object.defineProperty(navigator,'onLine',{get:()=>false})"});c.call('Page.reload')
def wait(expr):
 end=time.monotonic()+15
 while time.monotonic()<end:
  if c.eval(expr):return
  time.sleep(.1)
 raise AssertionError(expr)
wait("document.documentElement.dataset.autoWrap==='true'")
wait("!!document.querySelector('.vditor-ir [contenteditable]')")
c.eval("document.querySelectorAll('dialog[open]').forEach(d=>d.close())")
c.eval("document.querySelector('.note-row').click()")
wait("!!document.querySelector('.vditor-ir [contenteditable]')")
long='https://example.test/'+('long_segment_'*50)
md='# 换行验收\n\n```text\n    '+long+'\n\t'+('长代码'*80)+'\n```\n\n| 内容 | 备注 |\n| --- | --- |\n| '+long+' | '+('表格内容'*50)+' |\n'
c.eval("document.getElementById('source-mode').click();let s=document.querySelector('.vditor-sv');s.focus();s.select()")
c.call('Input.insertText',{'text':md});wait("document.querySelector('.vditor-sv').value.includes('换行验收')");c.eval('window.notebookFlush()')
c.eval("document.getElementById('preview-mode').click()")
wait("!!document.querySelector('.vditor-ir table')")
before=c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
c.eval("window.wrapCode=()=>[...document.querySelectorAll('.vditor-ir__preview > code,pre.vditor-ir__marker--pre code')].find(e=>e.getClientRects().length)")
for width in (390,1440):
 c.call('Emulation.setDeviceMetricsOverride',{'width':width,'height':844,'deviceScaleFactor':1,'mobile':width<761})
 for theme in ('light','dark'):
  if c.eval('document.documentElement.dataset.theme')!=theme:c.eval("document.getElementById('theme').click()")
  time.sleep(.15)
  assert c.eval("wrapCode().scrollWidth<=wrapCode().clientWidth+1"),(width,theme,'code')
  assert c.eval("(()=>{let t=document.querySelector('.vditor-ir table');return t.scrollWidth<=t.clientWidth+1&&getComputedStyle(t).tableLayout==='fixed'})()"),(width,theme,'table')
  assert c.eval("document.documentElement.scrollWidth<=innerWidth")
c.eval("document.querySelector('.vditor-ir__preview > code').click()")
assert c.eval("getComputedStyle(wrapCode()).whiteSpace==='pre-wrap'&&wrapCode().scrollWidth<=wrapCode().clientWidth+1")
# Preview toggle, cancel, persisted disabled preference, reload, and restore defaults.
def dialog():
 wait("!!document.querySelector('.notebook-display button')")
 c.eval("document.querySelector('.notebook-display button').click()")
 wait("!!document.getElementById('display-dialog')")
dialog();c.eval("var f=document.querySelector('#display-dialog form');f.elements.autoWrap.checked=false;f.dispatchEvent(new Event('input',{bubbles:true}))")
assert c.eval("document.documentElement.dataset.autoWrap==='false'")
assert c.eval("wrapCode().scrollWidth>wrapCode().clientWidth")
c.eval("document.querySelector('#display-dialog [data-cancel]').click()")
wait("document.documentElement.dataset.autoWrap==='true'")
dialog();c.eval("var f=document.querySelector('#display-dialog form');f.elements.autoWrap.checked=false;f.requestSubmit(f.querySelector('.primary'))")
wait("!document.getElementById('display-dialog')")
c.call('Page.reload');wait("document.documentElement.dataset.autoWrap==='false'")
c.eval("document.querySelectorAll('dialog[open]').forEach(d=>d.close())")
dialog();c.eval("document.querySelector('#display-dialog [data-reset]').click()")
assert c.eval("document.querySelector('#display-dialog input[name=autoWrap]').checked")
c.eval("var f=document.querySelector('#display-dialog form');f.requestSubmit(f.querySelector('.primary'))")
wait("!document.getElementById('display-dialog')")
assert c.eval("document.documentElement.dataset.autoWrap==='true'")
assert c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")==before
c.screenshot('/tmp/notes-auto-wrap.png')
print('PASS default wrapping, long code/URLs/table cells fit 390/1440px in both themes, editing wraps, disable/cancel/save/reload/reset, Markdown bytes unchanged')
