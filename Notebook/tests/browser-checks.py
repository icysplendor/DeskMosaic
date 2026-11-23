import socket, json, base64, os, struct, urllib.request, time
class CDP:
 def __init__(self):
  tabs=json.load(urllib.request.urlopen('http://127.0.0.1:9223/json'));url=next(x['webSocketDebuggerUrl'] for x in tabs if x['type']=='page');path=url.split('9223')[1]
  self.s=socket.create_connection(('127.0.0.1',9223));self.s.settimeout(20);key=base64.b64encode(os.urandom(16)).decode();self.s.sendall(f'GET {path} HTTP/1.1\r\nHost: 127.0.0.1:9223\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n'.encode());header=b''
  while b'\r\n\r\n' not in header:header+=self.s.recv(1)
  self.seq=0;self.events=[]
 def exact(self,n):
  result=b''
  while len(result)<n:result+=self.s.recv(n-len(result))
  return result
 def recv(self):
  h=self.exact(2);n=h[1]&127
  if n==126:n=struct.unpack('!H',self.exact(2))[0]
  elif n==127:n=struct.unpack('!Q',self.exact(8))[0]
  data=self.exact(n)
  return json.loads(data)
 def call(self,method,params={}):
  self.seq+=1;data=json.dumps({'id':self.seq,'method':method,'params':params}).encode();mask=os.urandom(4);n=len(data);header=bytes([129,128+n]) if n<126 else bytes([129,254])+struct.pack('!H',n) if n<65536 else bytes([129,255])+struct.pack('!Q',n);self.s.sendall(header+mask+bytes(c^mask[i%4] for i,c in enumerate(data)))
  while True:
   result=self.recv()
   if result.get('id')==self.seq:
    if 'error'in result:raise Exception(result['error'])
    return result.get('result',{})
   self.events.append(result)
 def eval(self,expression):
  result=self.call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True})
  if 'exceptionDetails'in result:raise Exception(result['exceptionDetails'])
  return result.get('result',{}).get('value')
 def screenshot(self,path):
  result=self.call('Page.captureScreenshot',{'format':'png','captureBeyondViewport':False});open(path,'wb').write(base64.b64decode(result['data']))

# Run against the isolated tests/preview-server.mjs and headless Chrome on 9223.
# This exercises the actual bundled Vditor; production notes are never opened.
from pathlib import Path
import uuid
c=CDP();c.call('Page.enable');c.call('Network.enable');c.call('Network.setBypassServiceWorker',{'bypass':True});c.call('Page.navigate',{'url':'about:blank'})
root=Path('/tmp/notebook-ui-test-library')
import shutil
if root.exists(): shutil.rmtree(root)
root.mkdir()
book=str(uuid.uuid4());note=str(uuid.uuid4())
folder=root/'books'/book/note
(folder/'reference').mkdir(parents=True);(folder/'.trash').mkdir()
(root/'books'/book/'book.json').write_text(json.dumps({'id':book,'title':'工作记录','created':int(time.time()*1000)}))
(folder/'note.json').write_text(json.dumps({'id':note,'title':'代码与附件验收','created':int(time.time()*1000),'updated':int(time.time()*1000)}))
md='# 项目部署记录\n\n本文记录部署步骤，正文可以直接修改。\n\n## 部署步骤\n\n- [x] 拉取镜像\n- [ ] 验证服务\n\n```bash\n  docker compose pull\n\tdocker compose up -d\n\n  echo "ready"\n```\n\n| 服务 | 状态 |\n| --- | --- |\n| API 服务 | 已启动 |\n| 数据库 | 已连接 |\n'
(folder/'main.md').write_text(md);(root/'.state.json').write_text(json.dumps({'grace':5000,'lastSync':0,'conflicts':0}))
c.call('Runtime.enable');c.call('Log.enable')
c.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':1024,'deviceScaleFactor':1,'mobile':False})
c.call('Emulation.setTimezoneOverride',{'timezoneId':'Asia/Shanghai'})
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"window.chrome=window.chrome||{};window.chrome.webview={postMessage:async message=>{try{const response=await fetch('/_bridge',{method:'POST',body:JSON.stringify(message)});const value=await response.json();window.nativeReply(message.id,value.result,value.error);}catch(error){window.nativeReply(message.id,null,error.message);}}};"})
c.call('Log.clear');c.events=[]
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/index.html'});time.sleep(1)
c.eval("[...document.querySelectorAll('.note-row')].find(x=>x.textContent.includes('代码与附件验收')).click()");time.sleep(1)
c.eval("document.querySelector('.vditor-copy button:not(.code-popout)').click()");time.sleep(.2)
assert Path('/tmp/notebook-ui-clipboard').read_text()=='  docker compose pull\n\tdocker compose up -d\n\n  echo "ready"'
# Clicking the code body must edit directly, with no redundant edit button.
assert c.eval("[...document.querySelectorAll('.vditor-copy button:not(.code-popout)')].every(x=>x.textContent!=='编辑')")
# Code edit exposes one editable surface, preserving the underlying Markdown.
c.eval("document.querySelector('.vditor-ir__preview > code').click()");time.sleep(.3)
assert c.eval("(()=>{let b=document.querySelector('[data-type=code-block].vditor-ir__node--expand');return !!b && getComputedStyle(b.querySelector('.vditor-ir__preview')).display!=='none' && getComputedStyle(b.querySelector('.vditor-ir__preview > code')).display==='none' && getComputedStyle(b,'::before').content==='none';})()")
c.screenshot('/tmp/notebook-code-edit.png')
c.call('Input.dispatchKeyEvent',{'type':'keyDown','key':'Enter','code':'Enter','windowsVirtualKeyCode':13});c.call('Input.dispatchKeyEvent',{'type':'keyUp','key':'Enter','code':'Enter','windowsVirtualKeyCode':13})
c.call('Input.insertText',{'text':'  echo editor-verified'});time.sleep(.6)
c.eval('window.notebookFlush()')
assert 'echo editor-verified' in (folder/'main.md').read_text()
assert c.eval("(()=>{let h=document.querySelector('.vditor-ir h1');return h.getBoundingClientRect().top-document.querySelector('.vditor-toolbar').getBoundingClientRect().bottom;})()")<=26
c.eval("document.querySelector('#book-list button').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:80,clientY:300}))");time.sleep(.2)
assert c.eval("document.querySelector('[role=menu]').textContent.includes('删除笔记本')")
c.eval("document.querySelector('[role=menu]').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))")
c.eval("document.querySelector('.note-row').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:250,clientY:250}))");time.sleep(.2)
assert c.eval("document.querySelector('[role=menu]').textContent.includes('移动到笔记本')")
c.eval("document.querySelector('[role=menu]').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))")
assert c.eval("!document.getElementById('choose-root')")

# Use the actual file-picker handler, FileReader and native bridge.
c.eval("(()=>{let dt=new DataTransfer();dt.items.add(new File(['attachment bytes'], '部署说明.txt',{type:'text/plain'}));let input=document.getElementById('file-input');input.files=dt.files;input.dispatchEvent(new Event('change'));})()")
time.sleep(1)
files=list((folder/'reference').glob('*.txt'));assert len(files)==1
assert files[0].read_text()=='attachment bytes'
c.eval("document.getElementById('source-mode').click()");time.sleep(.2)
with_link=c.eval("document.querySelector('.vditor-sv').value")
assert 'reference/' in with_link and '部署说明' in with_link
# Removal through source editing, delayed move, then real Undo across modes.
c.eval("var source=document.querySelector('.vditor-sv');source.focus();source.select()")
without_link=__import__('re').sub(r'\[部署说明.txt\]\(reference/[^)]+\)\n?','',with_link)
c.call('Input.insertText',{'text':without_link});time.sleep(.7)
assert files[0].exists(),'grace period was skipped'
time.sleep(5.3);c.eval("document.getElementById('maintenance').click()");time.sleep(.6)
assert not files[0].exists() and (folder/'.trash'/files[0].name).exists()
c.eval("document.getElementById('preview-mode').click();document.querySelector('.vditor-toolbar [data-type=undo]').click()");time.sleep(.7)
assert files[0].exists() and not (folder/'.trash'/files[0].name).exists(),'Undo did not restore attachment'
time.sleep(7.1) # Wait for the informational maintenance toast to disappear.
# Capture the same polished document and attachment drawer in both themes.
c.eval("document.getElementById('attachments').click()");time.sleep(.4)
if c.eval("document.documentElement.dataset.theme")!='light':c.eval("document.getElementById('theme').click()");time.sleep(.3)
c.screenshot('/tmp/notebook-verified-light.png')
c.eval("document.getElementById('theme').click()");time.sleep(.4);c.screenshot('/tmp/notebook-verified-dark.png')
# Minimum native window dimensions: persistent controls must stay visible.
c.call('Emulation.setDeviceMetricsOverride',{'width':900,'height':600,'deviceScaleFactor':1,'mobile':False});time.sleep(.3)
assert c.eval("document.documentElement.scrollWidth<=innerWidth")
assert c.eval("document.getElementById('attachments').getBoundingClientRect().right<=innerWidth")
assert c.eval("document.querySelector('footer').getBoundingClientRect().bottom<=innerHeight"),'footer hidden at minimum size'
c.screenshot('/tmp/notebook-verified-small.png')
# An external editor changes main.md while this editor has unsaved text.
c.eval("document.getElementById('source-mode').click();var source=document.querySelector('.vditor-sv');source.focus();source.select()")
c.call('Input.insertText',{'text':'窗口未保存内容\n'})
(folder/'main.md').write_text('外部程序修改\n');time.sleep(.8)
assert '外部修改' in c.eval("document.getElementById('save-status').textContent")
c.eval("document.getElementById('save-status').click()");time.sleep(.2)
assert c.eval("window.notebookFlush().then(()=>false,()=>true)"),'exit bypassed an unresolved external edit'
c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='载入磁盘版本').click()");time.sleep(.6)
assert c.eval("document.querySelector('.vditor-sv').value").strip()=='外部程序修改'
assert any('窗口未保存内容' in p.read_text() for p in (root/'.quarantine').glob('*/window.md'))
c.eval("window.notebookFlush()")
# Image display must resolve native bytes without changing the Markdown URL.
c.eval("(()=>{let dt=new DataTransfer();dt.items.add(new File([Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j6dEAAAAASUVORK5CYII='),x=>x.charCodeAt(0))],'diagram 100%.png',{type:'image/png'}));let f=document.getElementById('file-input');f.files=dt.files;f.dispatchEvent(new Event('change'));})()")
time.sleep(.7);c.eval("document.getElementById('preview-mode').click()");time.sleep(.6)
assert c.eval("document.querySelector('#editor img').naturalWidth")==1,'image attachment did not render'
c.eval("document.getElementById('source-mode').click()");image_md=c.eval("document.querySelector('.vditor-sv').value")
assert '![diagram 100%.png](reference/' in image_md and 'data:image' not in image_md,'image URL leaked into Markdown'
# Reopening the note uses cached native image bytes before rendering it.
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/index.html'});time.sleep(.8)
c.eval("document.querySelector('.note-row').click()");time.sleep(.8)
assert c.eval("document.querySelector('#editor img').naturalWidth")==1
# Editing adjacent prose spins the IR DOM again; the source must stay portable.
c.eval("(()=>{let p=document.querySelector('.vditor-ir p');let r=document.createRange();r.selectNodeContents(p);r.collapse(false);let selection=getSelection();selection.removeAllRanges();selection.addRange(r);document.querySelector('.vditor-ir [contenteditable]').focus();})()")
c.call('Input.insertText',{'text':'图片后的正文编辑'});time.sleep(.7)
c.eval("document.getElementById('source-mode').click()");portable=c.eval("document.querySelector('.vditor-sv').value")
assert '图片后的正文编辑' in portable and 'data:image' not in portable and '![diagram 100%.png](reference/' in portable
errors=[e for e in c.events if e.get('method')=='Runtime.exceptionThrown' or e.get('method')=='Log.entryAdded' and e['params']['entry']['level']=='error']
assert not errors,json.dumps(errors,ensure_ascii=False)
print('PASS exact code copy; binary import; grace period; trash move; cross-mode Undo restoration; light/dark drawer; 900x600 layout; native image preview; relative image Markdown; zero browser errors')
# User-facing notebook navigation and compact rows.
assert c.eval("document.querySelector('[data-filter=recent]').nextElementSibling.id")=='quick-notes'
assert c.eval("document.querySelector('.note-row').getBoundingClientRect().height")<=64
c.eval("document.getElementById('quick-notes').click()");time.sleep(.8)
assert c.eval("document.getElementById('list-title').textContent")=='快速笔记'
quick_folder=root/'books'/'quick-notes'
quick_notes=list(quick_folder.glob('*/note.json'));assert len(quick_notes)==1
quick_id=json.loads(quick_notes[0].read_text())['id']
c.eval("window.notebookNavigate('quick')");time.sleep(.3)
assert len(list(quick_folder.glob('*/note.json')))==1,'Repeated quick command created duplicate notes'
c.eval("window.notebookFlush()")
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/index.html'});time.sleep(.9)
assert json.loads((root/'.state.json').read_text())['lastPage']['noteId']==quick_id
assert c.eval("document.getElementById('list-title').textContent")=='快速笔记'
c.eval("document.querySelector('[data-filter=all]').click()");time.sleep(.2)
c.eval("[...document.querySelectorAll('.note-row')].find(x=>x.textContent.includes('代码与附件验收')).click()");time.sleep(.6)
c.eval("window.notebookFlush()")
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/index.html'});time.sleep(.9)
assert c.eval("document.querySelector('#editor img').naturalWidth")==1,'Last note was not restored automatically'
c.eval("document.getElementById('preview-mode').click()");time.sleep(.3)
tip=c.eval("(()=>{let el=document.querySelector('.vditor-toolbar [data-type=ordered-list]');let r=el.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,cls:el.className};})()")
assert 'vditor-tooltipped__s' in tip['cls']
c.call('Input.dispatchMouseEvent',{'type':'mouseMoved','x':tip['x'],'y':tip['y']});time.sleep(.6)
assert c.eval("(()=>{let el=document.querySelector('.vditor-toolbar [data-type=ordered-list]');let tip=getComputedStyle(el,'::after');return tip.display!='none' && parseFloat(tip.top)>=el.getBoundingClientRect().height;})()")
c.screenshot('/tmp/notebook-tooltip-verified.png')
print('PASS compact rows; fixed Quick Notes position; first note creation; idempotent quick command; persistent last page; downward toolbar tooltip')

# Real notebook context menu: rename, cancel delete, delete, restore with attachment bytes.
c.eval("document.querySelector('#book-list button').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:80,clientY:300}));[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent==='重命名').click()");time.sleep(.2)
c.eval("document.getElementById('text-value').value='验收笔记本';document.getElementById('text-dialog').close('ok')");time.sleep(.3)
assert json.loads((root/'books'/book/'book.json').read_text())['title']=='验收笔记本'
def delete_book():
 c.eval("document.querySelector('#book-list button').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:80,clientY:300}));[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent.startsWith('删除笔记本')).click()");time.sleep(.2)
delete_book();c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='取消').click()");time.sleep(.2)
assert folder.exists()
delete_book();c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='移入回收站').click()");time.sleep(.5)
assert not folder.exists() and (root/'.deleted-books'/book/note/'main.md').exists()
c.eval("document.getElementById('trash').click()");time.sleep(.3)
c.eval("[...document.querySelectorAll('#deleted-list button')].find(x=>x.textContent==='恢复笔记本及笔记').click()");time.sleep(.4)
assert folder.exists() and (folder/'reference').exists()
print('PASS code editing persists Markdown; notebook context rename/cancel/delete/full restore')

# Code blocks: real mouse interaction, persistent language and immediate blank creation.
def click(selector):
 pos=c.eval(f"(()=>{{let e=document.querySelector({json.dumps(selector)});e.scrollIntoView({{block:'nearest'}});let r=e.getBoundingClientRect();return {{x:r.x+r.width/2,y:r.y+r.height/2}};}})()")
 for typ in ['mousePressed','mouseReleased']:c.call('Input.dispatchMouseEvent',{'type':typ,**pos,'button':'left','clickCount':1})
c.eval("document.getElementById('trash-close').click();document.querySelector('[data-filter=all]').click();document.querySelector('.note-row').click()");time.sleep(.5)
c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.focus();s.select()")
c.call('Input.insertText',{'text':'正文\n\n```javascript\nconst answer = 42;\n```\n\n尾行\n'});time.sleep(.4)
c.eval("document.getElementById('preview-mode').click()");time.sleep(.5)
header=c.eval("document.querySelector('.vditor-copy').getBoundingClientRect().top")
click('.vditor-ir__preview > code');time.sleep(.3)
assert abs(c.eval("document.querySelector('.vditor-copy').getBoundingClientRect().top")-header)<1,'header shifted'
assert c.eval("getComputedStyle(document.querySelector('.code-language')).display")!='none'
sel=c.eval("(()=>{let s=getSelection();window.savedAnchor=s.anchorNode;return {offset:s.anchorOffset,text:s.anchorNode.textContent};})()")
click('.vditor-copy button:not(.code-popout)');time.sleep(.3)
assert c.eval("getSelection().anchorNode===window.savedAnchor"),'copy moved caret node'
assert c.eval("getSelection().anchorOffset")==sel['offset'],'copy moved caret offset'
assert Path('/tmp/notebook-ui-clipboard').read_text()=='const answer = 42;'
c.eval("var l=document.querySelector('.code-language');l.value='python';l.dispatchEvent(new Event('change',{bubbles:true}))");time.sleep(.4)
c.eval('window.notebookFlush()')
assert c.eval("document.querySelector('.code-language').value")=='python'
c.eval("document.querySelector('.vditor-toolbar [data-type=undo]').click()");time.sleep(.3)
assert c.eval("document.querySelector('.code-language').value")=='javascript','language undo lost prior value'
c.eval("document.querySelector('.vditor-toolbar [data-type=redo]').click()");time.sleep(.3)
assert c.eval("document.querySelector('.code-language').value")=='python','language redo lost selected value'
c.eval("document.getElementById('source-mode').click()")
assert '```python\nconst answer = 42;' in c.eval("document.querySelector('.vditor-sv').value"),'language not saved'
c.eval("document.getElementById('preview-mode').click();var p=[...document.querySelectorAll('.vditor-ir p')].at(-1);document.querySelector('.vditor-ir [contenteditable]').focus();var r=document.createRange();r.selectNodeContents(p);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r)")
click('.vditor-toolbar [data-type=code]');time.sleep(.5)
assert c.eval("!!getSelection().anchorNode.parentElement.closest('.vditor-ir__marker--pre')"),'new code did not focus source'
assert c.eval("document.querySelectorAll('.code-language').length")==2,'new code missing'
assert c.eval("[...document.querySelectorAll('.code-language')].at(-1).selectedOptions[0].textContent")=='自动（txt）'
c.call('Input.insertText',{'text':'const value = 1;'});time.sleep(.6)
assert c.eval("[...document.querySelectorAll('.code-language')].at(-1).selectedOptions[0].textContent")=='自动（javascript）','automatic detection did not update'
c.eval('window.notebookFlush()')
assert 'const value = 1;' in c.eval("(()=>{document.getElementById('source-mode').click();return document.querySelector('.vditor-sv').value})()")
c.eval("document.getElementById('preview-mode').click()")
c.screenshot('/tmp/notebook-code-new-verified.png')
print('PASS stable language header; physical copy retains caret; change language persists; direct code creation; txt fallback; automatic language detection')

# Recycle page, confirmed purge and padding.
root=Path('/tmp/notebook-ui-test-library');notes=[p for p in root.glob('books/*/*/note.json') if json.loads(p.read_text())['title']=='代码与附件验收'];folder=notes[0].parent;note=folder.name
c.eval("document.querySelector('[data-filter=all]').click();[...document.querySelectorAll('.note-row')].find(x=>x.textContent.includes('代码与附件验收')).dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:250,clientY:200}));[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent.startsWith('移入回收站')).click()");time.sleep(.3)
c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='移入回收站').click()");time.sleep(.5)
archive=root/'.deleted'/note;assert archive.exists() and not folder.exists()
c.eval("document.getElementById('trash').click()");time.sleep(.3)
assert c.eval("document.getElementById('workspace').classList.contains('trash-view')")
assert c.eval("!document.getElementById('trash-page').hidden && getComputedStyle(document.getElementById('notes')).display==='none'")
assert c.eval("document.getElementById('trash-page').getBoundingClientRect().width")>700
assert c.eval("document.querySelectorAll('#deleted-list .danger').length")==1
assert c.eval("parseFloat(getComputedStyle(document.querySelector('#deleted-list .trash-row')).paddingLeft)")>=16
assert c.eval("document.querySelector('#deleted-list strong').getBoundingClientRect().left-document.querySelector('#deleted-list .trash-row').getBoundingClientRect().left")>=16
c.screenshot('/tmp/notebook-trash-verified-dark.png')
c.eval("document.getElementById('theme').click()");time.sleep(.3);c.screenshot('/tmp/notebook-trash-verified-light.png')
c.eval("document.querySelector('#deleted-list .danger').click()");time.sleep(.2)
c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='取消').click()");time.sleep(.2)
assert archive.exists(),'Cancel erased archive'
c.eval("document.querySelector('#deleted-list .danger').click()");time.sleep(.2)
c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='永久删除').click()");time.sleep(.5)
assert not archive.exists(),'Permanent delete kept archived bytes'
assert c.eval("document.getElementById('deleted-list').textContent")=='回收站为空'
c.eval("window.notebookNavigate('quick')");time.sleep(.3)
assert c.eval("document.getElementById('trash-page').hidden"),'Quick navigation stayed in recycle page'
c.eval("document.getElementById('search').value='__no_matching_note__';document.getElementById('search').dispatchEvent(new Event('input'))");time.sleep(.2)
assert c.eval("parseFloat(getComputedStyle(document.querySelector('#note-list > p')).paddingLeft)")>=16
print('PASS main-frame recycle page; full-width layout; permanent delete cancel/confirm; archive bytes removed; empty-list padding; quick navigation returns from recycle page; light/dark screenshots')

# The code hotkey must bypass the language prompt just like the toolbar.
c.eval("document.getElementById('search').value='';document.getElementById('search').dispatchEvent(new Event('input'));document.querySelector('.note-row').click()");time.sleep(.4)
c.eval("document.querySelector('.vditor-ir [contenteditable]').focus()")
for typ in ['keyDown','keyUp']:c.call('Input.dispatchKeyEvent',{'type':typ,'key':'u','code':'KeyU','windowsVirtualKeyCode':85,'modifiers':2})
time.sleep(.4)
assert c.eval("!!getSelection().anchorNode.parentElement.closest('.vditor-ir__marker--pre')"),'code hotkey focused language'
assert c.eval("document.querySelectorAll('dialog[open]').length")==0
print('PASS code shortcut directly focuses code body')
# Selected code is retained; source-mode insertion stays at the source caret.
c.eval("document.getElementById('source-mode').click();var sv=document.querySelector('.vditor-sv');sv.focus();sv.select()")
c.call('Input.insertText',{'text':'前文\n\nconst selected = 2;\n\n后文\n'});time.sleep(.4)
c.eval("var sv=document.querySelector('.vditor-sv');var start=sv.value.indexOf('const selected');sv.setSelectionRange(start,start+'const selected = 2;'.length)")
click('.vditor-toolbar [data-type=code]');time.sleep(.4)
assert c.eval("document.getElementById('source-mode').classList.contains('active')"),'code insertion changed editor mode'
assert c.eval("document.querySelector('.vditor-sv').value.includes('```\\nconst selected = 2;\\n```')"),'code insertion erased selected text'
assert c.eval("document.querySelector('.vditor-sv').value.slice(0,document.querySelector('.vditor-sv').selectionStart).endsWith('const selected = 2;')"),'source insertion moved caret out of code'
c.eval('window.notebookFlush()')
print('PASS source insertion preserves selected code, mode and caret')
errors=[e for e in c.events if e.get('method')=='Runtime.exceptionThrown' or e.get('method')=='Log.entryAdded' and e['params']['entry']['level']=='error']
assert not errors,json.dumps(errors,ensure_ascii=False)

# Enter confirms creation; cancel and IME composition keep their normal semantics.
def enter_name(name):
 assert c.eval("(()=>{let input=document.getElementById('text-value');return input.selectionStart===0 && input.selectionEnd===input.value.length && document.activeElement===input})()"),'initial name is not selected'
 c.call('Input.insertText',{'text':name})
 for typ in ['keyDown','keyUp']:c.call('Input.dispatchKeyEvent',{'type':typ,'key':'Enter','code':'Enter','windowsVirtualKeyCode':13,**({'text':'\r','unmodifiedText':'\r'} if typ=='keyDown' else {})})
 time.sleep(.5)
c.eval("document.getElementById('new-book').click()");time.sleep(.2)
enter_name('回车创建笔记本')
assert c.eval("document.getElementById('book-list').textContent.includes('回车创建笔记本')"),'Enter cancelled notebook creation'
c.eval("document.getElementById('new-note').click()");time.sleep(.2)
enter_name('回车创建笔记')
assert c.eval("document.getElementById('note-list').textContent.includes('回车创建笔记')"),'Enter cancelled note creation'
created=[p.parent for p in root.glob('books/*/*/note.json') if json.loads(p.read_text())['title']=='回车创建笔记'][0]
assert (created/'main.md').read_text()=='','New note repeated title in body'
c.eval("document.getElementById('new-book').click()");time.sleep(.2)
c.eval("document.getElementById('text-value').value='取消创建';document.getElementById('text-cancel').click()");time.sleep(.3)
assert not c.eval("document.getElementById('book-list').textContent.includes('取消创建')")
c.eval("document.getElementById('new-note').click()");time.sleep(.2)
for typ in ['keyDown','keyUp']:c.call('Input.dispatchKeyEvent',{'type':typ,'key':'Escape','code':'Escape','windowsVirtualKeyCode':27})
time.sleep(.3)
assert c.eval("document.querySelectorAll('dialog[open]').length")==0
print('PASS physical Enter creates notebook/note; empty new body; cancel and Escape still cancel')

# Measure the same code bodies and following prose through repeated real clicks.
c.eval("document.getElementById('source-mode').click();var sv=document.querySelector('.vditor-sv');sv.focus();sv.select()")
code_fixture='正文\n\n```txt\n单行代码 '+('long_token '*100)+'\n```\n\n```bash\n  line one\n\tline two\n\n  line four\n```\n\n```txt\n'+'\n'.join('long line '+str(i) for i in range(80))+'\n```\n\n结束正文\n'
c.call('Input.insertText',{'text':code_fixture});time.sleep(.4)
c.eval("document.getElementById('preview-mode').click()");time.sleep(.5)
def metrics(index):
 return c.eval(f"(()=>{{let b=[...document.querySelectorAll('.vditor-ir__node[data-type=code-block]')][{index}],h=b.querySelector('.vditor-copy'),body=b.querySelector(b.classList.contains('vditor-ir__node--expand')?'.vditor-ir__marker--pre > code':'.vditor-ir__preview > code');let r=b.getBoundingClientRect();return {{top:r.top,height:r.height,width:r.width,head:h.getBoundingClientRect().top-r.top,body:body.getBoundingClientRect().top-r.top,bodyHeight:body.getBoundingClientRect().height,padding:getComputedStyle(body).padding,border:getComputedStyle(b).borderWidth, radius:getComputedStyle(b).borderRadius}}}})()")
def mouse_click(selector):
 pos=c.eval(f"(()=>{{let e=document.querySelector({json.dumps(selector)});e.scrollIntoView({{block:'nearest'}});let r=e.getBoundingClientRect();if(e.closest('.vditor-ir__node')) window.beforeEditTop=e.closest('.vditor-ir__node').getBoundingClientRect().top;return {{x:r.x+r.width/2,y:r.y+r.height/2}};}})()")
 for typ in ['mousePressed','mouseReleased']:c.call('Input.dispatchMouseEvent',{'type':typ,**pos,'button':'left','clickCount':1})
for width,height in [(900,600),(1920,1080)]:
 c.call('Emulation.setDeviceMetricsOverride',{'width':width,'height':height,'deviceScaleFactor':1,'mobile':False});time.sleep(.3)
 assert c.eval("document.getElementById('note-toolbar').getBoundingClientRect().height")<=46,'note toolbar too tall'
 assert c.eval("document.querySelector('.navigation').getBoundingClientRect().height")<=38,'navigation too tall'
 assert c.eval("parseFloat(getComputedStyle(document.getElementById('books')).gap)")<=4
 assert c.eval("document.documentElement.scrollWidth<=innerWidth")
 editor_width=c.eval("document.getElementById('editor').getBoundingClientRect().width")
 block_width=metrics(0)['width']
 assert editor_width-block_width<=80,'wide editor still capped to a fixed width'
 for index in range(3):
  for repeat in range(2):
   mouse_click('.vditor-ir p');time.sleep(.1)
   before=metrics(index)
   mouse_click(f'.vditor-ir__node[data-type=code-block]:nth-of-type({index+1}) .vditor-ir__preview > code');time.sleep(.15)
   after=metrics(index)
   assert c.eval("(()=>{let s=getSelection(),r=s.getRangeAt(0).getBoundingClientRect(),body=s.anchorNode.parentElement.getBoundingClientRect();return r.top>=body.top && r.bottom<=body.bottom+1 && r.left>=body.left && r.right<=body.right+1;})()"),'code caret remains outside its scroll area'
   assert abs(after['top']-c.eval('window.beforeEditTop'))<1,'editing scrolled the visible block'
   for key in ['height','width','head','body','bodyHeight']:
    assert abs(before[key]-after[key])<1,(width,index,key,before,after)
   assert before['padding']==after['padding'] and before['border']==after['border']
 c.screenshot('/tmp/notebook-compact-'+str(width)+'-'+c.eval("document.documentElement.dataset.theme")+'.png')
 c.eval("document.getElementById('theme').click()");time.sleep(.3)
 c.screenshot('/tmp/notebook-compact-'+str(width)+'-'+c.eval("document.documentElement.dataset.theme")+'.png')
print('PASS stable code/body/header dimensions across repeated clicks; one/multiple/long lines; compact toolbar/sidebar; full-width 1920px editor; light/dark and 900px layout')

errors=[e for e in c.events if e.get('method')=='Runtime.exceptionThrown' or e.get('method')=='Log.entryAdded' and e['params']['entry']['level']=='error']
assert not errors,json.dumps(errors,ensure_ascii=False)

# Test reading position with unequal rendered/source heights, not global scroll percentages.
fixture=''
for i in range(80):
 if i%10==0:fixture+='## Chapter '+str(i)+'\n\n'
 fixture+='ANCHOR-'+str(i).zfill(3)+' reading paragraph\n\n'
 if i%7==0:fixture+='```txt\nsmall code\nsecond line\n\nlast line\n```\n\n'
 if i%9==0:fixture+='| item | state |\n| --- | --- |\n| first | ready |\n| second | waiting |\n\n'
 if i%11==0:fixture+='- first item\n- second item\n\n'
 if i==40:fixture+='```txt\n'+'\n'.join('BIG-CODE-'+str(n).zfill(3) for n in range(100))+'\n```\n\n'
c.eval("document.getElementById('source-mode').click();var sv=document.querySelector('.vditor-sv');sv.focus();sv.select()")
c.call('Input.insertText',{'text':fixture});time.sleep(.5)
c.eval("document.getElementById('preview-mode').click()");time.sleep(.3)
def ir_top(marker):
 return c.eval(f"(()=>{{let root=document.querySelector('.vditor-ir [contenteditable]'),p=[...root.children].find(x=>x.tagName==='P'&&x.textContent.includes({json.dumps(marker)}));let r=p.getBoundingClientRect();return r.top-root.getBoundingClientRect().top-parseFloat(getComputedStyle(root).paddingTop);}})()")
def source_top():
 return c.eval("(()=>{let s=document.querySelector('.vditor-sv'),css=getComputedStyle(s);let line=Math.floor((s.scrollTop)/parseFloat(css.lineHeight));return s.value.split('\\n')[line]})()")
for width in [900,1920]:
 c.call('Emulation.setDeviceMetricsOverride',{'width':width,'height':800,'deviceScaleFactor':1,'mobile':False});time.sleep(.2)
 c.eval("(()=>{let r=document.querySelector('.vditor-ir [contenteditable]'),p=[...r.children].find(x=>x.tagName==='P'&&x.textContent.includes('ANCHOR-055'));r.scrollTop+=p.getBoundingClientRect().top-r.getBoundingClientRect().top-parseFloat(getComputedStyle(r).paddingTop)})()")
 for repeat in range(3):
  c.eval("document.getElementById('source-mode').click()");time.sleep(.25)
  assert 'ANCHOR-055' in source_top(),('source center differs',width,source_top())
  c.eval("document.getElementById('preview-mode').click()");time.sleep(.25)
  assert abs(ir_top('ANCHOR-055'))<=4,(width,ir_top('ANCHOR-055'))
 # Independently scroll the code body, then center its visible body in the page.
 c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]'),b=[...root.querySelectorAll('[data-type=code-block]')].find(x=>x.textContent.includes('BIG-CODE-')),code=b.querySelector('.vditor-ir__preview > code');code.scrollTop=25*parseFloat(getComputedStyle(code).lineHeight);let rect=code.getBoundingClientRect();root.scrollTop+=rect.top+parseFloat(getComputedStyle(code).paddingTop)-root.getBoundingClientRect().top-parseFloat(getComputedStyle(root).paddingTop);})()")
 expected=c.eval("(()=>{let code=[...document.querySelectorAll('.vditor-ir__preview > code')].find(x=>x.textContent.includes('BIG-CODE-')),css=getComputedStyle(code);return Math.floor((code.scrollTop)/parseFloat(css.lineHeight));})()")
 c.eval("document.getElementById('source-mode').click()");time.sleep(.25)
 assert source_top()=='BIG-CODE-'+str(expected).zfill(3),(expected,source_top())
 c.eval("document.getElementById('preview-mode').click()");time.sleep(.25)
 actual=c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]'),code=[...root.querySelectorAll('.vditor-ir__preview > code')].find(x=>x.textContent.includes('BIG-CODE-')),css=getComputedStyle(code);return Math.floor((root.getBoundingClientRect().top+parseFloat(getComputedStyle(root).paddingTop)-code.getBoundingClientRect().top+code.scrollTop-parseFloat(css.paddingTop))/parseFloat(css.lineHeight));})()")
 assert abs(actual-expected)<=1,(expected,actual)
 # A user scroll immediately after switching must cancel pending corrections.
 c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.dispatchEvent(new WheelEvent('wheel',{bubbles:true,deltaY:120}));s.scrollTop+=120;window.userScroll=s.scrollTop")
 time.sleep(.8)
 assert abs(c.eval("document.querySelector('.vditor-sv').scrollTop-window.userScroll"))<1,'restoration fought user scroll'
 c.eval("document.getElementById('preview-mode').click()");time.sleep(.2)
print('PASS first visible paragraph preserved across repeated switches at 900/1920px; long code visible line preserved; user scroll cancels corrections')

# A long wrapped paragraph keeps its relative visible line, including Unicode.
c.eval("document.getElementById('source-mode').click();var sv=document.querySelector('.vditor-sv');sv.focus();sv.select()")
wrapped='开头\n\n'+('before paragraph\n\n'*25)+'WRAPPED '+('中文内容 with **emphasis** and words '*140)+'\n\n'+('after paragraph\n\n'*25)
c.call('Input.insertText',{'text':wrapped});time.sleep(.4)
c.eval("document.getElementById('preview-mode').click()");time.sleep(.25)
c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]'),p=[...root.children].find(x=>x.textContent.includes('WRAPPED')),rect=p.getBoundingClientRect();root.scrollTop+=rect.top+rect.height*.37-root.getBoundingClientRect().top-parseFloat(getComputedStyle(root).paddingTop);})()")
def wrapped_offset():
 return c.eval("(()=>{let root=document.querySelector('.vditor-ir [contenteditable]'),p=[...root.children].find(x=>x.textContent.includes('WRAPPED'));return root.getBoundingClientRect().top+parseFloat(getComputedStyle(root).paddingTop)-p.getBoundingClientRect().top;})()")
initial=wrapped_offset()
for repeat in range(3):
 c.eval("document.getElementById('source-mode').click()");time.sleep(.25)
 c.eval("document.getElementById('preview-mode').click()");time.sleep(.25)
 assert abs(wrapped_offset()-initial)<5,('wrapped paragraph drifted',initial,wrapped_offset())
assert c.eval("[...document.body.children].filter(x=>x.style.left==='-100000px').length")==0,'measurement mirror leaked'
# Fast repeated switches should cancel older restorations.
c.eval("document.getElementById('source-mode').click();document.getElementById('preview-mode').click();document.getElementById('source-mode').click();document.getElementById('preview-mode').click()")
time.sleep(.3)
assert abs(wrapped_offset()-initial)<5
c.eval('window.notebookFlush()')
assert c.eval("document.querySelectorAll('dialog[open]').length")==0
print('PASS wrapped Unicode paragraph; rapid mode switches; measurement cleanup; content can still save')
errors=[e for e in c.events if e.get('method')=='Runtime.exceptionThrown' or e.get('method')=='Log.entryAdded' and e['params']['entry']['level']=='error']
assert not errors,json.dumps(errors,ensure_ascii=False)
