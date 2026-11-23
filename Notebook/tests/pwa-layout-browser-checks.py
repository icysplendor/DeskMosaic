"""Responsive desktop/tablet/mobile navigation and light/dark shared editor."""
from pathlib import Path
import json,time,urllib.request
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Network.enable')
c.call('Network.emulateNetworkConditions',{'offline':False,'latency':0,'downloadThroughput':-1,'uploadThroughput':-1})
c.eval("localStorage.removeItem('test-offline')")
def wait(expr):
 end=time.monotonic()+12
 while time.monotonic()<end:
  if c.eval(expr):return
  time.sleep(.1)
 raise AssertionError(expr+' '+str(c.eval("document.getElementById('toast')?.textContent")))
# Prior offline test ends at login with the original durable edits retained.
c.eval("(()=>{let f=document.querySelector('#web-login form');f.elements.user.value='test';f.elements.password.value='service-test';f.requestSubmit()})()")
wait("!!document.querySelector('.vditor-ir p')")
# Login starts a background sync. Wait for its retained conflict before testing
# focus/keyboard layout; its final refresh may otherwise reset the edit toggle.
wait("document.getElementById('sync-status').textContent.includes('未完成')")
# Preserve the conflict dialog's data but close it for layout inspection.
c.eval("document.querySelectorAll('dialog[open]').forEach(d=>d.close())")
for width,height in [(390,844),(768,1024),(1440,900)]:
 c.call('Emulation.setDeviceMetricsOverride',{'width':width,'height':height,'deviceScaleFactor':1,'mobile':width<761})
 # Metrics acknowledge before the visual viewport resize listener updates CSS.
 wait(f"innerWidth==={width}&&innerHeight==={height}&&Math.abs(parseFloat(document.documentElement.style.getPropertyValue('--web-height'))-{height})<1")
 for theme in ('light','dark'):
  if c.eval('document.documentElement.dataset.theme')!=theme:c.eval("document.getElementById('theme').click()")
  wait("document.documentElement.dataset.theme==="+json.dumps(theme))
  assert c.eval("document.documentElement.scrollWidth<=innerWidth"),(width,theme)
  assert c.eval("document.querySelector('meta[name=theme-color]').content") == ('#111b2b' if theme=='dark' else '#f8fbff')
  if width<761:
   assert c.eval("getComputedStyle(document.getElementById('mobile-nav')).display")=='grid'
   assert c.eval("getComputedStyle(document.getElementById('web-books')).display")=='none'
   assert c.eval("(()=>{let n=document.getElementById('mobile-nav').getBoundingClientRect(),w=document.getElementById('workspace').getBoundingClientRect();return w.bottom<=n.top+1&&Math.abs(n.bottom-innerHeight)<2&&[...document.querySelectorAll('#mobile-nav button')].every(b=>b.getBoundingClientRect().height>=44)})()")
   assert c.eval("document.querySelector('.vditor-toolbar').getBoundingClientRect().height")==0
   assert c.eval("document.querySelector('.vditor-ir [contenteditable]').getAttribute('contenteditable')")=='false'
   reading_style=c.eval("(()=>{let s=getComputedStyle(document.querySelector('.vditor-ir pre.vditor-reset'));return [s.opacity,s.color,s.fontFamily,s.fontSize,s.lineHeight]})()")
   assert reading_style[0]=='1',reading_style
   c.eval("document.getElementById('mobile-display').click()")
   wait("!!document.querySelector('dialog[open] input[name=lineHeight]')")
   c.eval("document.querySelector('dialog[open] button[data-cancel]').click()")
   c.eval("document.getElementById('mobile-edit').click()")
   wait("document.body.dataset.mobileEditing==='true'")
   assert c.eval("document.querySelector('.vditor-ir [contenteditable]').getAttribute('contenteditable')")=='true'
   assert c.eval("(()=>{let s=getComputedStyle(document.querySelector('.vditor-ir pre.vditor-reset'));return [s.opacity,s.color,s.fontFamily,s.fontSize,s.lineHeight]})()")==reading_style
   assert 0<c.eval("document.querySelector('.vditor-toolbar').getBoundingClientRect().height")<50
   # iOS pans its visual viewport independently of layout viewport height.
   c.eval("window.savedViewport={height:visualViewport.height,offsetTop:visualViewport.offsetTop};Object.defineProperty(visualViewport,'height',{configurable:true,value:400});Object.defineProperty(visualViewport,'offsetTop',{configurable:true,value:120});document.querySelector('.vditor-ir [contenteditable]').focus();visualViewport.dispatchEvent(new Event('scroll'))")
   wait("(()=>{let n=document.getElementById('mobile-nav').getBoundingClientRect(),w=document.getElementById('workspace').getBoundingClientRect();return document.body.dataset.keyboard==='true'&&Math.abs(n.bottom-520)<2&&Math.abs(w.bottom-n.top)<2})()")
   c.eval("delete visualViewport.height;delete visualViewport.offsetTop;visualViewport.dispatchEvent(new Event('resize'));document.getElementById('mobile-edit').click()")
   wait("document.body.dataset.mobileEditing==='false'")
   c.eval("document.getElementById('mobile-notes').click()")
   wait("document.body.dataset.mobilePage==='notes'")
   assert c.eval("document.getElementById('notes').getBoundingClientRect().width")==width
   c.eval("document.getElementById('mobile-books').click()")
   wait("document.body.dataset.mobilePage==='books'")
   c.eval("document.querySelector('#book-list button').click()")
   wait("document.body.dataset.mobilePage==='notes'")
   c.eval("document.querySelector('.note-row').click()")
   wait("document.body.dataset.mobilePage==='editor'")
  else:
   assert c.eval("getComputedStyle(document.getElementById('mobile-nav')).display")=='none'
   assert c.eval("(()=>{let b=document.getElementById('books').getBoundingClientRect(),n=document.getElementById('notes').getBoundingClientRect(),m=document.querySelector('#workspace > main').getBoundingClientRect();return b.width>100&&n.width>150&&m.width>250&&b.right<=n.left+1&&n.right<=m.left+1})()")
  c.screenshot('/tmp/notes-pwa-'+str(width)+'-'+theme+'.png')
# A failed durable write must not publish an in-memory replacement.
assert c.eval("(async()=>{let io=window.notebookBrowser.io,path='books/mobile/n3/main.md',before=await io.read(path),original=io.db.transaction;io.db.transaction=()=>{throw new DOMException('Storage quota exceeded','QuotaExceededError')};let failed=false;try{await io.write(path,'must not replace')}catch{failed=true}finally{io.db.transaction=original}return failed&&await io.read(path)===before})()")
print('PASS desktop/tablet layout, mobile read-only/edit toggle, display settings, panned keyboard viewport and bottom navigation in both themes; failed durable write keeps old content')

# Resolve the previously retained text conflict, then test a lazy attachment move
# against a concurrent desktop update of that attachment.
c.eval('window.notebookSync()')
wait("document.getElementById('sync-status').textContent.includes('未完成')")
c.eval("document.getElementById('remote-status').click()")
wait("document.getElementById('sync-report-dialog').open")
c.eval("document.getElementById('sync-report-conflicts').click()")
wait("document.getElementById('conflict-dialog').open")
c.eval("document.querySelector('#conflict-list button').click()")
wait("!document.getElementById('conflict-dialog').open")
c.eval('window.notebookSync()')
c.eval("(async()=>{let io=window.notebookBrowser.io;await io.readBinary('books/mobile/n1/reference/file.pdf');await io.move('books/mobile/n1','books/quick-notes/n1')})()")
urllib.request.urlopen('http://127.0.0.1:4175/_test/attachment-change').read()
c.eval('window.notebookSync()')
wait("document.getElementById('sync-status').textContent.includes('未完成')")
c.eval("document.getElementById('remote-status').click()")
wait("document.getElementById('sync-report-dialog').open")
c.eval("document.getElementById('sync-report-conflicts').click()")
wait("document.getElementById('conflict-dialog').open")
remote=json.load(urllib.request.urlopen('http://127.0.0.1:4175/_test'))['files']
assert remote['books/mobile/n1/reference/file.pdf']=='%PDF-1.4 desktop newer bytes'
assert 'books/mobile/n1/note.json' in remote and 'books/mobile/n1/main.md' in remote
assert remote['books/quick-notes/n1/reference/file.pdf']=='%PDF-1.4 attachment 1'
print('PASS lazy attachment move preserves original note metadata when desktop concurrently changes its bytes; both copies retained')

# A partial inventory cannot discard an uncached attachment. Finishing a pass
# clears only bytes that match the checkpoint, including concurrent local edits.
assert c.eval("(async()=>{let io=window.notebookBrowser.io,path='books/mobile/n2/reference/file.pdf',before=io.entries.get(path),list=[...io.entries.values()].filter(r=>r.remote&&!r.dir&&r.path!==path).map(r=>({path:r.path,etag:r.etag}));list.errors=[{path:'books/mobile/n2/',status:403}];await io.inventory(list);return before?.data===null&&io.entries.get(path)===before})()")
assert c.eval("(async()=>{let io=window.notebookBrowser.io,path='books/mobile/n2/main.md';await io.write(path,'latest local edit after upload');await io.finishSync();return io.entries.get(path).dirty&&await io.read(path)==='latest local edit after upload'&&await io.pendingCount()>0})()")
print('PASS partial inventory retains uncached attachments and concurrent local edits stay durably pending after sync completion')
