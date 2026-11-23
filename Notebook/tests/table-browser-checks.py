"""Real table sizing, cell context operations and text formatting with pinned Vditor."""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('image-browser-checks.py').read_text().split('for theme in')[0])
fixture='before\n\n| Name | State |\n| :--- | ---: |\n| **Alpha** | [site](https://example.com) |\n| Beta | `ready` |\n\nafter\n'
def load(md):
 c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.focus();s.select()")
 c.call('Input.insertText',{'text':md});time.sleep(.2)
 c.eval("document.getElementById('preview-mode').click()")
 time.sleep(.2)
def saved():
 c.eval('window.notebookFlush()')
 return (folder/'main.md').read_text()
def menu(cell, title):
 c.eval("(()=>{let cell="+cell+";cell.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:600,clientY:240}));})()")
 wait("!!document.querySelector('[role=menu]')")
 if '标题' in title:
  c.eval("[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent==='标题…').click()")
  wait("[...document.querySelectorAll('[role=menu] button')].some(x=>x.textContent==="+json.dumps(title)+")")
 c.eval("[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent==="+json.dumps(title)+").click()")
 time.sleep(.15)
def count():return c.eval("[...document.querySelectorAll('.vditor-ir table')].map(t=>[t.rows.length,t.rows[0].cells.length])")
load(fixture)
# The anchored grid inserts only on cell selection; Escape cancels.
c.eval("document.querySelector('.vditor-toolbar [data-type=table]').click()")
wait("!!document.getElementById('table-picker')")
assert count()==[[3,2]]
c.eval("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))")
wait("!document.getElementById('table-picker')")
assert count()==[[3,2]]
c.eval("(()=>{let p=[...document.querySelectorAll('.vditor-ir p')].at(-1),r=document.createRange();r.selectNodeContents(p);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r);document.querySelector('.vditor-toolbar [data-type=table]').click()})()")
wait("!!document.getElementById('table-picker')")
c.eval("document.querySelector('#table-picker [data-row=\"4\"][data-column=\"3\"]').click()")
wait("!document.getElementById('table-picker')")
assert count()==[[3,2],[4,3]],count()
assert c.eval("(()=>{let n=getSelection().anchorNode;return !!(n.nodeType===1?n:n.parentElement).closest('th')})()")
assert 'after' in saved()
print('PASS table size picker, cancel, grid click creates exact 4×3 table and focuses header')
# Commands use the clicked cell rather than an unrelated prior caret.
first="document.querySelector('.vditor-ir table')"
menu(first+".rows[1].cells[1]",'上方插入行');assert count()[0]==[4,2]
menu(first+".rows[1].cells[1]",'下方插入行');assert count()[0]==[5,2]
menu(first+".rows[2].cells[1]",'左侧插入列');assert count()[0]==[5,3]
menu(first+".rows[2].cells[1]",'右侧插入列');assert count()[0]==[5,4]
text=saved();assert '**Alpha**' in text and '[site](https://example.com)' in text and '`ready`' in text
menu(first+".rows[2].cells[1]",'列居中');assert c.eval(first+".rows[0].cells[1].getAttribute('align')")=='center'
assert __import__('re').search(r':-+:', saved().replace(' ',''))
menu(first+".rows[2].cells[1]",'删除当前列');assert count()[0]==[5,3]
menu(first+".rows[1].cells[0]",'删除当前行');assert count()[0]==[4,3]
# Undo/redo preserve individual operations; source round-trip remains Markdown.
c.eval("document.querySelector('.vditor-toolbar [data-type=undo]').click()")
assert count()[0]==[5,3]
c.eval("document.querySelector('.vditor-toolbar [data-type=redo]').click()")
assert count()[0]==[4,3]
menu(first+".rows[0].cells[0]",'上方插入行');assert count()[0]==[5,3]
assert c.eval(first+".rows[1].cells[0].textContent")=='Name'
menu(first+".rows[0].cells[0]",'删除当前行');assert c.eval(first+".rows[0].cells[0].textContent")=='Name'
assert c.eval(first+".tHead.rows.length") == 1
text=saved();assert '**Alpha**' in text and '[site](https://example.com)' in text
c.eval("document.getElementById('source-mode').click()")
assert '<table' not in c.eval("document.querySelector('.vditor-sv').value")
c.eval("document.getElementById('preview-mode').click()")
print('PASS clicked-cell row/column insert/delete, alignment, header promotion, inline content retention and cross-mode history')
# Whole-table deletion prompts, cancel leaves content, confirm is undoable.
menu(first+".rows[0].cells[0]",'删除表格…');key('Escape',27);assert len(count())==2
menu(first+".rows[0].cells[0]",'删除表格…')
c.eval("[...document.querySelectorAll('dialog[open] button')].find(x=>x.textContent==='删除').click()")
wait("document.querySelectorAll('.vditor-ir table').length===1")
c.eval("document.querySelector('.vditor-toolbar [data-type=undo]').click()")
assert len(count())==2
for title,expected in [('加粗','**selected**'),('斜体','*selected*'),('删除线','~~selected~~'),('行内代码','`selected`'),('二级标题','## selected'),('无序列表','* selected'),('有序列表','1. selected'),('任务/多选框','* [ ] selected'),('引用','> selected')]:
 load('before\n\nselected\n\nafter\n')
 c.eval("(()=>{let p=[...document.querySelectorAll('.vditor-ir p')].find(x=>x.textContent==='selected'),r=document.createRange();r.selectNodeContents(p);getSelection().removeAllRanges();getSelection().addRange(r)})()")
 menu("[...document.querySelectorAll('.vditor-ir p')].find(x=>x.textContent==='selected')",title)
 text=saved();assert expected in __import__('re').sub(r' +',' ',text),(title,text)
 assert 'before' in text and 'after' in text
 c.eval("document.querySelector('.vditor-toolbar [data-type=undo]').click()")
 assert 'selected' in saved() and expected not in saved()
print('PASS table deletion confirmation/cancel/Undo; text context bold/italic/strike/code/headings/lists/quote preserve selection and Undo')

# The single-column edge must confirm instead of silently removing the table.
load('| Only |\n| --- |\n| value |\n')
menu(first+'.rows[1].cells[0]', '删除当前列');key('Escape',27);assert count()==[[2,1]]
menu(first+'.rows[1].cells[0]', '清空单元格');assert 'value' not in saved()
c.eval("document.querySelector('.vditor-toolbar [data-type=undo]').click()")
assert 'value' in saved()
# Source formatting also restores the textarea's selection after opening a menu.
c.eval("document.getElementById('source-mode').click();var s=document.querySelector('.vditor-sv');s.focus();var n=s.value.indexOf('value');s.setSelectionRange(n,n+5)")
menu("document.querySelector('.vditor-sv')",'加粗')
assert '**value**' in saved()
print('PASS last-column deletion confirmation, clear cell/Undo and source-mode selection formatting')

# Capture both themes and verify the compact menus at the minimum window size.
c.eval("document.getElementById('preview-mode').click()")
c.call('Emulation.setDeviceMetricsOverride',{'width':900,'height':600,'deviceScaleFactor':1,'mobile':False})
for theme in ('light','dark'):
 if c.eval('document.documentElement.dataset.theme')!=theme:c.eval("document.getElementById('theme').click()")
 wait("document.documentElement.dataset.theme==="+json.dumps(theme))
 c.eval("document.querySelector('.vditor-toolbar [data-type=table]').click()")
 wait("!!document.getElementById('table-picker')")
 assert c.eval("(()=>{let r=document.querySelector('#table-picker').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})()")
 c.screenshot('/tmp/notebook-table-'+theme+'.png')
 key('Escape',27);wait("!document.getElementById('table-picker')")
 c.eval("document.querySelector('.vditor-ir th').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:890,clientY:590}))")
 assert c.eval("(()=>{let r=document.querySelector('[role=menu]').getBoundingClientRect();return r.right<=innerWidth&&r.bottom<=innerHeight&&r.top>=0})()")
 c.screenshot('/tmp/notebook-table-menu-'+theme+'.png')
 key('Escape',27)
print('PASS light/dark grid popover and scrollable cell menu fit 900×600 window')
