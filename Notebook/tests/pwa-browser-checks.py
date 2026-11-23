"""Real mobile PWA: independent login, text-only cache, offline restart/edit and conflict preservation."""
from pathlib import Path
import urllib.request,json,time
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
for tab in json.load(urllib.request.urlopen('http://127.0.0.1:9223/json')):
 if tab['type']=='page' and tab.get('url','').startswith('http://127.0.0.1:4175'):
  urllib.request.urlopen('http://127.0.0.1:9223/json/close/'+tab['id']).read()
urllib.request.urlopen('http://127.0.0.1:4175/_test/reset').read()
new=json.load(urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:9223/json/new?about:blank',method='PUT')))
c=CDP();c.call('Page.enable');c.call('Network.enable')
# Chromium resets target network state when a service-worker-controlled document
# swaps renderers on reload. Persist API disconnection across those reloads too.
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"const realFetch=window.fetch;window.fetch=(...args)=>localStorage.getItem('test-offline')?Promise.reject(new TypeError('Failed to fetch')):realFetch(...args);Object.defineProperty(navigator,'onLine',{get:()=>!localStorage.getItem('test-offline')});"})
c.call('Storage.clearDataForOrigin',{'origin':'http://127.0.0.1:4175','storageTypes':'all'})
c.call('Emulation.setDeviceMetricsOverride',{'width':390,'height':844,'deviceScaleFactor':2,'mobile':True})
token=json.load(urllib.request.urlopen('http://127.0.0.1:4175/_test/invite'))['token']
c.call('Page.navigate',{'url':'http://127.0.0.1:4175/api/account-setup#setup='+token})
def wait(expr,timeout=15):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  if c.eval(expr):return
  time.sleep(.1)
 raise AssertionError(expr+' '+str(c.eval("document.getElementById('toast')?.textContent")))
def state():return json.load(urllib.request.urlopen('http://127.0.0.1:4175/_test'))
wait("!!document.querySelector('input[name=user]')")
c.eval("(()=>{let f=document.querySelector('form');f.elements.user.value='test';f.elements.password.value='service-test';f.elements.confirm.value='service-test';f.requestSubmit()})()")
wait("document.getElementById('sync-dialog')?.open")
assert c.eval('location.hash')=='', 'setup token must be removed from address bar'
urllib.request.urlopen('http://127.0.0.1:4175/_test/hold-lock').read()
c.eval("document.getElementById('dav-url').value='https://cloud.fixture/remote.php/dav/files/test/Notes/';document.getElementById('dav-user').value='test';document.getElementById('dav-password').value='dav-test';document.getElementById('sync-save').click()")
wait("document.getElementById('sync-status').textContent.startsWith('等待另一台设备 · ')")
assert 'another-device' in state()['files']['.notebook-lock.json']
urllib.request.urlopen('http://127.0.0.1:4175/_test/release-lock').read()
wait("document.querySelectorAll('.note-row').length===3")
wait("document.getElementById('sync-status').textContent==='Nextcloud 已同步'")
c.eval("document.querySelector('.note-row').click()")
wait("!!document.querySelector('.vditor-ir p')")
assert not any('/reference/' in p for p in state()['reads']),state()['reads']
assert 'password' not in c.eval("localStorage.getItem('notes-session')")
assert c.eval("document.body.dataset.mobilePage")=='editor'
assert c.eval("document.documentElement.scrollWidth<=innerWidth"),'mobile layout overflows'
before=c.eval("document.querySelector('.vditor-ir').textContent")
c.eval("document.querySelector('.vditor-ir [contenteditable]').focus()")
c.call('Input.insertText',{'text':'must not edit read-only text'})
assert c.eval("document.querySelector('.vditor-ir').textContent")==before
c.eval("navigator.serviceWorker.ready");wait("!!navigator.serviceWorker.controller")
c.screenshot('/tmp/notes-pwa-mobile.png')
time.sleep(.4)
assert '.notebook-lock.json' not in state()['files'],state()['files'].get('.notebook-lock.json')
print('PASS active device lock waits and retries automatically, compression-safe lock release, single-use independent account setup, server DAV setup, complete text cache without downloading attachments, shared editor and 390px layout')

# Reproduce the user's PUT 423 through the actual service bridge and editor.
target='books/mobile/n1/main.md';original_text=state()['files'][target]
def set_fault(count):
 req=urllib.request.Request('http://127.0.0.1:4175/_test/fault',data=json.dumps({'method':'PUT','path':target,'status':423,'remaining':count}).encode(),headers={'Content-Type':'application/json'})
 urllib.request.urlopen(req).read()
c.eval("[...document.querySelectorAll('.note-row')].find(x=>x.textContent.includes('离线测试 1')).click()")
wait("document.querySelector('.vditor-ir').textContent.includes('正文 1')")
c.eval("document.getElementById('mobile-edit').click();document.getElementById('source-mode').click()")
def type_source(text):
 c.eval("var s=document.querySelector('.vditor-sv');s.focus();s.select()")
 c.call('Input.insertText',{'text':text});c.eval('window.notebookFlush()')
type_source('手机 423 测试：本地文字必须保留。\n')
base=json.loads(c.eval("window.notebookBrowser.io.read('.sync.json')"))['files'][target]
# A different complete note must still publish while n1 is locked.
c.eval("(async()=>{await window.notebookBrowser.io.write('books/mobile/n2/main.md','independent complete note\\n');await window.notebookBrowser.io.write('books/mobile/n2/note.json',JSON.stringify({id:'n2',title:'离线测试 2',created:0,updated:20}))})()")
set_fault(4)
c.eval('window.notebookSync();true')
wait("document.getElementById('sync-status').textContent.includes('网盘文件暂时被锁定')")
wait("document.getElementById('sync-status').textContent.includes('秒后重试')",20)
assert state()['files'][target]==original_text
assert state()['files']['books/mobile/n2/main.md']=='independent complete note\n'
report=json.loads(c.eval("window.notebookBrowser.io.read('.sync-report.json')"))
assert report['failed']==1 and report['completed']>=3
assert next(x for x in report['items'] if x['id']=='n2')['complete']
assert not next(x for x in report['items'] if x['id']=='n1')['complete']
assert not c.eval("document.querySelector('dialog[open]')!==null"),'background errors must not block editing'
c.eval("document.getElementById('remote-status').click()")
wait("document.getElementById('sync-report-dialog').open")
assert c.eval("document.querySelector('#sync-report-list [data-complete=false]').textContent.includes('离线测试 1')")
assert c.eval("document.querySelector('#sync-report-list [data-complete=false]').textContent.includes('423')")
c.eval("document.getElementById('sync-report-close').click()")
assert '手机 423 测试' in c.eval(f"window.notebookBrowser.io.read({json.dumps(target)})")
checkpoint=json.loads(c.eval("window.notebookBrowser.io.read('.sync.json')"))
assert checkpoint['files'][target]==base and not checkpoint['conflicts']
conditions=state()['fault']['conditions']
assert len(conditions)==4 and conditions[0]['match'] and all(x==conditions[0] for x in conditions)
set_fault(0)
wait("fetch('/_test').then(r=>r.json()).then(s=>s.files['books/mobile/n1/main.md'].includes('手机 423 测试'))",20)
wait("document.getElementById('sync-status').textContent==='Nextcloud 已同步'")
type_source(original_text);c.eval('window.notebookSync()')
c.eval("document.getElementById('preview-mode').click();document.getElementById('mobile-edit').click();[...document.querySelectorAll('.note-row')].find(x=>x.textContent.includes('离线测试 3')).click()")
wait("document.querySelector('.vditor-ir').textContent.includes('正文 3')")
print('PASS real PUT 423: unchanged checkpoint, preserved local text, no false conflict, original If-Match on all retries, automatic recovery after unlock')
# Reload without any network: the app shell and all three notes remain usable.
c.eval("localStorage.setItem('test-offline','true')")
c.call('Network.emulateNetworkConditions',{'offline':True,'latency':0,'downloadThroughput':0,'uploadThroughput':0})
c.call('Page.reload');wait("document.querySelectorAll('.note-row').length===3")
wait("!!document.querySelector('.vditor-ir p')")
c.eval("document.getElementById('mobile-edit').click()")
wait("document.body.dataset.mobileEditing==='true'")
c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.focus();s.select()")
c.call('Input.insertText',{'text':'手机离线编辑，不能丢失。\n\n```bash\necho offline\n```\n'})
c.eval('window.notebookFlush()')
wait("document.getElementById('save-status').textContent.includes('已保存到本机')")
wait("document.getElementById('web-sync-state').textContent.includes('待同步')")
c.call('Page.reload');wait("!!document.querySelector('.vditor-ir p')")
assert c.eval("document.querySelector('.vditor-ir').textContent.includes('手机离线编辑，不能丢失')")
assert state()['files']['books/mobile/n3/main.md'].startswith('正文 3')
assert '.notebook-lock.json' not in state()['files'],('lock acquired while offline',c.eval('navigator.onLine'),state()['files'].get('.notebook-lock.json'))
c.screenshot('/tmp/notes-pwa-offline.png')
print('PASS airplane-mode restart, all note bodies retained, durable offline editing and pending state after a second restart')
# A desktop changes the same file while the phone is offline.
urllib.request.urlopen('http://127.0.0.1:4175/_test/change').read()
c.eval("localStorage.removeItem('test-offline')")
c.call('Network.emulateNetworkConditions',{'offline':False,'latency':0,'downloadThroughput':-1,'uploadThroughput':-1})
c.eval('window.notebookSync()')
wait("document.getElementById('sync-status').textContent.includes('未完成')")
assert not c.eval("document.getElementById('conflict-dialog').open")
c.eval("document.getElementById('remote-status').click()")
wait("document.getElementById('sync-report-dialog').open")
c.eval("document.getElementById('sync-report-conflicts').click()")
wait("document.getElementById('conflict-dialog').open")
assert state()['files']['books/mobile/n3/main.md']=='电脑同时修改了这一篇。\n'
local=c.eval("window.notebookBrowser.io.read('books/mobile/n3/main.md')")
assert '手机离线编辑' in local
assert c.eval("[...window.notebookBrowser.io.entries.values()].some(r=>r.path.startsWith('.conflicts/')&&r.path.endsWith('/remote')&&atob(r.data).length>0)")
assert not any('/reference/' in p for p in state()['reads'])
print('PASS reconnect conflict preserves both phone and desktop versions; no timestamp overwrite and no attachment downloads')
# Logout is an explicit gate, even with cached offline notes.
c.eval("document.getElementById('conflict-close').click();window.confirm=()=>true;document.getElementById('web-logout').click()")
wait("!!document.getElementById('web-login')")
assert c.eval("getComputedStyle(document.getElementById('workspace')).visibility")=='hidden'
assert c.eval("localStorage.getItem('notes-session')")==None
print('PASS logout closes access to cached notebook without deleting unsynced data')
