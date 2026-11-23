"""Context clipboard in both modes; duplicate imports and direct recycle actions."""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('image-browser-checks.py').read_text().split('for theme in')[0].replace('4173','4174'))
def source(md):
 c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.focus();s.select()")
 c.call('Input.insertText',{'text':md});time.sleep(.2)
def saved():
 c.eval('window.notebookFlush()');return (folder/'main.md').read_text()
def menu(selector,title):
 c.eval(selector+".dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:620,clientY:230}))")
 wait("!!document.querySelector('[role=menu]')")
 c.eval("[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent==="+json.dumps(title)+").click()")
 time.sleep(.25)
for mode in ('preview','source'):
 source('before\n\nselected 中文\n\nafter\n')
 if mode=='preview':
  c.eval("document.getElementById('preview-mode').click()")
  selector="[...document.querySelectorAll('.vditor-ir p')].find(x=>x.textContent==='selected 中文')"
  c.eval("(()=>{let p="+selector+",r=document.createRange();r.selectNodeContents(p);getSelection().removeAllRanges();getSelection().addRange(r)})()")
 else:
  selector="document.querySelector('.vditor-sv')"
  c.eval("var s=document.querySelector('.vditor-sv'),n=s.value.indexOf('selected');s.focus();s.setSelectionRange(n,n+'selected 中文'.length)")
 menu(selector,'复制');assert Path('/tmp/notebook-ui-clipboard').read_text()=='selected 中文'
 assert c.eval("document.activeElement.closest('#editor')!==null")
 assert 'selected 中文' in saved()
 menu(selector,'剪切');assert 'selected 中文' not in saved()
 # The clicked paragraph may have become empty after cutting.
 if mode=='preview':selector="document.querySelector('.vditor-ir [contenteditable]')"
 menu(selector,'粘贴');assert 'selected 中文' in saved()
 c.eval("document.querySelector('.vditor-toolbar [data-type=undo]').click()")
 assert 'selected 中文' not in saved()
 print('PASS '+mode+' context copy/cut/paste and Undo preserve selected text and caret')
c.eval("document.getElementById('preview-mode').click()")
for repeat in range(2):
 c.eval("(()=>{let d=new DataTransfer();d.items.add(new File(['original PDF bytes'],'repeat.pdf',{type:'application/pdf'}));let f=document.getElementById('file-input');f.files=d.files;f.dispatchEvent(new Event('change'));})()")
 wait("document.querySelectorAll('.notebook-attachment-link').length==="+str(repeat+1))
 saved()
assert len(list((folder/'reference').glob('*.pdf')))==1
c.eval("document.getElementById('attachments').click()")
wait("[...document.querySelectorAll('.attachment-row')].filter(x=>x.textContent.includes('repeat.pdf')).length===1")
row="[...document.querySelectorAll('.attachment-row')].find(x=>x.textContent.includes('repeat.pdf'))"
assert '正文正在引用' in c.eval(row+'.textContent')
assert '立即回收' not in c.eval(row+'.textContent')
source('before\n\nafter\n');saved()
c.eval("document.getElementById('attachments').click()") # close then reopen to refresh
c.eval("document.getElementById('attachments').click()")
c.eval("document.querySelector('[data-attachment-tab=pending]').click()")
wait("[...document.querySelectorAll('.attachment-row')].some(x=>x.textContent.includes('repeat.pdf')&&x.textContent.includes('立即回收'))")
c.eval("[..."+row+".querySelectorAll('button')].find(x=>x.textContent==='立即回收').click()")
wait("![...document.querySelectorAll('.attachment-row')].some(x=>x.textContent.includes('repeat.pdf'))")
assert len(list((folder/'.trash').glob('*.pdf')))==1
c.eval("document.querySelector('[data-attachment-tab=trash]').click()")
wait("[...document.querySelectorAll('.attachment-row')].some(x=>x.textContent.includes('永久删除'))")
# Cancel and accept permanent removal from this drawer, without navigating away.
c.eval("[...document.querySelectorAll('.attachment-row button')].find(x=>x.textContent==='永久删除').click()")
key('Escape',27);time.sleep(.1)
assert len(list((folder/'.trash').glob('*.pdf')))==1
c.eval("[...document.querySelectorAll('.attachment-row button')].find(x=>x.textContent==='永久删除').click()")
c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='永久删除').click()")
wait("![...document.querySelectorAll('.attachment-row')].some(x=>x.textContent.includes('repeat.pdf'))")
assert len(list((folder/'.trash').glob('*.pdf')))==0
print('PASS repeated file has one physical copy/list entry; referenced file protected; direct recycle and permanent delete cancel/confirm')
