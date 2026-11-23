"""Run after PWA browser/layout checks. Keyboard and mobile beforeinput semantics."""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Network.enable')
c.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':900,'deviceScaleFactor':1,'mobile':False})
c.eval("(async()=>{for(let r of await navigator.serviceWorker.getRegistrations())await r.unregister();for(let k of await caches.keys())await caches.delete(k);localStorage.setItem('test-offline','true')})()")
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"Object.defineProperty(navigator,'onLine',{get:()=>false})"})
c.call('Page.reload',{'ignoreCache':True})
def wait(expr):
 end=time.monotonic()+15
 while time.monotonic()<end:
  if c.eval(expr):return
  time.sleep(.1)
 raise AssertionError(expr)
wait("!!document.querySelector('.vditor-ir [contenteditable]')")
c.eval("document.querySelectorAll('dialog[open]').forEach(d=>d.close());document.querySelector('[data-filter=all]').click()")
def select(title):
 c.eval("[...document.querySelectorAll('.note-row')].find(x=>x.textContent.includes("+json.dumps(title)+")).click()")
 wait("document.getElementById('breadcrumb').textContent.includes("+json.dumps(title)+")&&!document.getElementById('editor').inert")
select('离线测试 3')
def source(md):
 c.eval("(()=>{document.getElementById('source-mode').click();let s=document.querySelector('.vditor-sv');s.focus();s.select()})()")
 c.call('Input.insertText',{'text':md})
 c.eval("document.getElementById('preview-mode').click()")
 time.sleep(.15)
def caret(selector='p',start=False):
 c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]');root.focus();let r=document.createRange();r.selectNodeContents(root.querySelector("+json.dumps(selector)+"));r.collapse("+str(start).lower()+");getSelection().removeAllRanges();getSelection().addRange(r)})()")
def enter(shift=False):
 c.call('Input.dispatchKeyEvent',{'type':'keyDown','key':'Enter','code':'Enter','windowsVirtualKeyCode':13,'modifiers':8 if shift else 0,'text':'\r','unmodifiedText':'\r'})
 c.call('Input.dispatchKeyEvent',{'type':'keyUp','key':'Enter','code':'Enter','windowsVirtualKeyCode':13,'modifiers':8 if shift else 0})
def paragraphs():return c.eval("document.querySelectorAll('.vditor-ir pre.vditor-reset > p').length")
def options(value=None,save=True):
 c.eval("document.querySelector('.notebook-display button').click()")
 wait("!!document.getElementById('display-dialog')")
 assert c.eval("document.querySelector('#display-dialog h2').textContent")=='选项'
 if value is not None:c.eval("(()=>{let f=document.querySelector('#display-dialog form');f.elements.enterBehavior.value="+json.dumps(value)+";f.dispatchEvent(new Event('input',{bubbles:true}))})()")
 c.eval("(()=>{let f=document.querySelector('#display-dialog form');"+("f.requestSubmit(f.querySelector('.primary'))" if save else "f.querySelector('[data-cancel]').click()")+"})()")
 wait("!document.getElementById('display-dialog')")
assert c.eval("document.querySelector('.notebook-display button').textContent")=='选项'
options('paragraph')
source('A\n');caret();enter();c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==2
source('A\n');caret();enter(True);c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==1
print('PASS default Enter creates paragraphs and Shift+Enter stays within a paragraph')
options('line',False)
source('A\n');caret();enter();c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==2
options('line')
source('A\n');caret();enter();c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==1
c.eval('window.notebookFlush()');saved=c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
assert 'A<br />B' in saved,saved
select('离线测试 2');select('离线测试 3');assert paragraphs()==1
source('');c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]');root.focus();let r=document.createRange();r.selectNodeContents(root);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r)})()")
for _ in range(3):enter()
c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==1
c.eval('window.notebookFlush()');saved=c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
assert saved.count('<br />')==3,saved
select('离线测试 2');select('离线测试 3');assert paragraphs()==1
source('A\n');caret()
for _ in range(3):enter()
c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==1
c.eval('window.notebookFlush()');saved=c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
assert saved.count('<br />')==3,saved
select('离线测试 2');select('离线测试 3');assert paragraphs()==1
source('A\n');caret();enter(True);c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==2
print('PASS inverse Enter/Shift+Enter behavior, cancel restores prior choice, save and note-switch retain intra-paragraph breaks')
c.eval('window.notebookFlush()');c.call('Page.reload');wait("document.querySelectorAll('.vditor-ir pre.vditor-reset > p').length===2")
c.eval("document.querySelector('.notebook-display button').click()")
wait("!!document.getElementById('display-dialog')")
assert c.eval("document.querySelector('#display-dialog select[name=enterBehavior]').value")=='line'
c.eval("document.querySelector('#display-dialog [data-cancel]').click()")
source('A\n');caret();enter();c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==1
# Mobile beforeinput-only keyboards and IME confirmation.
c.call('Emulation.setDeviceMetricsOverride',{'width':390,'height':844,'deviceScaleFactor':1,'mobile':True})
wait("document.body.dataset.mobileEditing==='false'")
assert c.eval("document.getElementById('mobile-display').textContent")=='选项'
c.eval("document.getElementById('mobile-edit').click()")
source('A\n');caret()
assert c.eval("document.querySelector('.vditor-ir [contenteditable]').dispatchEvent(new InputEvent('beforeinput',{inputType:'insertParagraph',bubbles:true,cancelable:true}))")==False
c.call('Input.insertText',{'text':'B'});time.sleep(.2);assert paragraphs()==1
assert c.eval("document.querySelector('.vditor-ir [contenteditable]').dispatchEvent(new InputEvent('beforeinput',{inputType:'insertParagraph',isComposing:true,bubbles:true,cancelable:true}))")==True
print('PASS persisted preference after restart, mobile beforeinput-only Enter and IME confirmation exclusion')
c.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':900,'deviceScaleFactor':1,'mobile':False})
# Special contexts keep native paragraph/list/table/code handling.
source('| A | B |\n| --- | --- |\n| C | D |\n');caret('td')
assert c.eval("document.querySelector('.vditor-ir [contenteditable]').dispatchEvent(new InputEvent('beforeinput',{inputType:'insertParagraph',bubbles:true,cancelable:true}))")==True
source('- A\n');caret('li p' if c.eval("!!document.querySelector('.vditor-ir li p')") else 'li');enter();c.call('Input.insertText',{'text':'B'});time.sleep(.2)
assert c.eval("document.querySelectorAll('.vditor-ir li').length")==2
source('```text\nA\n```\n');c.eval("document.querySelector('.vditor-ir__preview > code').click()")
caret('pre.vditor-ir__marker--pre code');enter();c.call('Input.insertText',{'text':'B'});time.sleep(.2)
c.eval('window.notebookFlush()');assert 'A\n\nB' in c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
source('A\n');c.eval("document.getElementById('source-mode').click();document.querySelector('.vditor-sv').focus();document.querySelector('.vditor-sv').setSelectionRange(1,1)")
enter();c.call('Input.insertText',{'text':'B'});assert c.eval("document.querySelector('.vditor-sv').value").startswith('A\nB')
c.eval("document.getElementById('preview-mode').click()")
options('paragraph')
print('PASS lists, code and source retain original rules; preference restored for subsequent regressions')
