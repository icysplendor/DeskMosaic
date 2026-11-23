"""Run after PWA browser/layout checks. Real Enter, save, switch and restart."""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Network.setBypassServiceWorker',{'bypass':True})
c.eval("(async()=>{for(let r of await navigator.serviceWorker.getRegistrations())await r.unregister();for(let k of await caches.keys())await caches.delete(k)})()")
c.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':900,'deviceScaleFactor':1,'mobile':False})
c.eval("localStorage.setItem('test-offline','true')")
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
 wait("document.getElementById('breadcrumb').textContent.includes("+json.dumps(title)+")")
select('离线测试 3')
def source(text):
 c.eval("(()=>{document.getElementById('source-mode').click();let s=document.querySelector('.vditor-sv');s.focus();s.select()})()")
 c.call('Input.insertText',{'text':text})
 c.eval("document.getElementById('preview-mode').click()")
 time.sleep(.2)
def blanks():
 return c.eval("[...document.querySelectorAll('.vditor-ir pre.vditor-reset > p')].filter(p=>!p.textContent.replace(/[\\s\\u200b]/g,'')).length")
def gap():
 return c.eval("(()=>{let p=[...document.querySelectorAll('.vditor-ir pre.vditor-reset > p')];return p.at(-1).getBoundingClientRect().top-p[0].getBoundingClientRect().bottom})()")
def switch():
 c.eval('window.notebookFlush()');select('离线测试 2');select('离线测试 3')
source('A\n\n\n\nB\n')
assert blanks()==2
initial=gap();assert initial>45,initial
switch();assert blanks()==2 and abs(gap()-initial)<1
assert c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')").count('<br />')==2
c.eval("document.getElementById('source-mode').click();document.getElementById('preview-mode').click()")
assert blanks()==2
print('PASS existing extra Markdown blank lines retain visible spacing across note and source/preview switches')
source('A\n')
c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]');root.focus();let r=document.createRange();r.selectNodeContents(root.querySelector('p'));r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r)})()")
for _ in range(4):
 c.call('Input.dispatchKeyEvent',{'type':'keyDown','key':'Enter','code':'Enter','windowsVirtualKeyCode':13,'text':'\r','unmodifiedText':'\r'})
 c.call('Input.dispatchKeyEvent',{'type':'keyUp','key':'Enter','code':'Enter','windowsVirtualKeyCode':13})
time.sleep(.4)
assert blanks()==4,blanks()
switch();assert blanks()==4,blanks()
c.call('Page.reload');wait("document.getElementById('breadcrumb').textContent.includes('离线测试 3')&&document.querySelectorAll('.vditor-ir pre.vditor-reset > p').length===5")
assert blanks()==4,blanks()
print('PASS four real Enter presses at end of note survive saving, switching and offline restart without growth')
c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]');root.focus();let r=document.createRange();r.selectNodeContents(root.querySelector('p:last-child'));r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r)})()")
c.call('Input.insertText',{'text':'B'});time.sleep(.3)
assert blanks()==3,blanks()
initial=gap();switch();assert blanks()==3 and abs(gap()-initial)<1
print('PASS text typed after multiple Enter presses preserves intermediate empty paragraphs and their measured spacing')
# New empty notes and automatic landing pads after code do not gain saved spacers.
for text in ['', '```text\na\n\n\nb\n```\n', '| A |\n| --- |\n| B |\n']:
 source(text);c.eval('window.notebookFlush()')
 saved=c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
 assert '<br />' not in saved,saved
 if '```' in text:assert 'a\n\n\nb' in saved
print('PASS empty notes, code-internal blank lines and table landing pads do not gain break markers')
