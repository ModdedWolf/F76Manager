(()=>{var Ke=class{constructor(){this.currentLanguage="en-US",this.loadedLanguages={},this.fallbackLanguage="en-US",this.pendingRequests={}}async init(){try{await this.loadLanguage(this.fallbackLanguage)}catch(i){console.error("[Translator] Critical: Failed to load fallback language.",i)}let t=typeof window.__F76_BOOT_LANGUAGE=="string"?window.__F76_BOOT_LANGUAGE:"",n=t&&t!=="auto"?t:this.detectLanguage();if(n!==this.fallbackLanguage)try{await this.loadLanguage(n),this.currentLanguage=n}catch(i){console.warn("[Translator] Failed to load detected language, sticking to fallback.",i)}this.applyDocumentLanguage()}applyDocumentLanguage(){if(typeof document>"u"||!document.documentElement)return;let t=String(this.currentLanguage||this.fallbackLanguage);document.documentElement.lang=t==="template-LANG"?this.fallbackLanguage:t}async setLanguage(t){if(t&&(t==="auto"&&(t=this.detectLanguage()),!(this.currentLanguage===t&&this.loadedLanguages[t]))){console.log(`[Translator] Switching to ${t}...`);try{await this.loadLanguage(t),this.currentLanguage=t,console.log(`[Translator] Language set to ${t}`)}catch{console.error(`[Translator] Failed to load ${t}, falling back to ${this.fallbackLanguage}`),this.currentLanguage=this.fallbackLanguage}this.applyDocumentLanguage()}}loadLanguage(t){return this.loadedLanguages[t]?Promise.resolve(this.loadedLanguages[t]):new Promise((n,i)=>{if(this.pendingRequests[t]={resolve:n,reject:i},window.chrome&&window.chrome.webview){console.log(`[Translator] Requesting ${t} from backend...`);let o=0,a=10;window.chrome.webview.postMessage({type:"LOAD_LANGUAGE",lang:t});let p=setInterval(()=>{if(!this.pendingRequests[t]){clearInterval(p);return}o++,o>=a?(clearInterval(p),this.pendingRequests[t]&&(console.warn(`[Translator] Timeout loading ${t} after ${a} attempts`),delete this.pendingRequests[t],i(new Error("Timeout")))):(console.log(`[Translator] Retry ${o}/${a} for ${t}...`),window.chrome.webview.postMessage({type:"LOAD_LANGUAGE",lang:t}))},500)}else console.warn("[Translator] No backend detected, trying fetch fallback..."),fetch(`locales/${t}.json`).then(o=>o.json()).then(o=>{this.loadedLanguages[t]=o,delete this.pendingRequests[t],n(o)}).catch(i)})}receiveLanguageContent(t,n,i){let o=this.pendingRequests[t];o&&(i?(console.error(`[Translator] Backend reported error for ${t}:`,i),o.reject(new Error(i))):(this.loadedLanguages[t]=n,o.resolve(n)),delete this.pendingRequests[t])}detectLanguage(){let t=navigator.language||navigator.userLanguage||"en-US";return t.startsWith("en")?"en-US":t.startsWith("ja")?"ja-JP":t.startsWith("fr")?"fr-FR":t.startsWith("de")?"de-DE":t.startsWith("es")?"es-ES":t.startsWith("it")?"it-IT":t.startsWith("pl")?"pl-PL":t.startsWith("ru")?"ru-RU":t.startsWith("pt")?"pt-BR":t.startsWith("zh")?t.toLowerCase().includes("tw")||t.toLowerCase().includes("hk")?"zh-TW":"zh-CN":t.startsWith("ko")?"ko-KR":"en-US"}t(t,...n){let i=null;return this.loadedLanguages[this.currentLanguage]&&(i=this.loadedLanguages[this.currentLanguage][t]),!i&&this.loadedLanguages[this.fallbackLanguage]&&(i=this.loadedLanguages[this.fallbackLanguage][t]),i||(i=t),n.length>0&&n.forEach((o,a)=>{i=i.split(`{${a}}`).join(String(o))}),i}},se=new Ke;var ge=null,Fe=null;function je(e){return String(e??"").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;")}function Ht(e){let t=String(e??""),n=/["'«「]([^"'»」]+)["'»」]/g,i=0,o,a=[];for(;(o=n.exec(t))!==null;)o.index>i&&a.push(je(t.slice(i,o.index))),a.push(`<code>${je(o[1])}</code>`),i=o.index+o[0].length;return i<t.length&&a.push(je(t.slice(i))),a.join("").replace(/\n/g,"<br>")}function mt(e=!1){let t=ge,n=Fe;ge=null,Fe=null;try{n?.()}catch{}let i=document.getElementById("confirm-modal");i&&i.classList.remove("active","confirm-modal--compact");let o=document.getElementById("confirm-alt");o&&(o.hidden=!0);let a=document.getElementById("confirm-prompt-clear");if(a&&(a.hidden=!0),t)try{t(e)}catch{}}function ht(e={}){return new Promise(t=>{let n=document.getElementById("confirm-modal"),i=document.getElementById("confirm-title"),o=document.getElementById("confirm-message"),a=document.getElementById("confirm-icon"),p=document.getElementById("confirm-ok"),s=document.getElementById("confirm-cancel"),d=document.getElementById("confirm-alt"),r=document.getElementById("confirm-close-top"),u=document.getElementById("confirm-prompt-wrap"),m=document.getElementById("confirm-prompt-input"),$=document.getElementById("confirm-prompt-clear"),E=document.getElementById("confirm-prompt-label");if(!n||!i||!o||!p||!s){console.warn("[appConfirm] #confirm-modal elements missing"),t(!1);return}mt(!1);let v=window._t,_=!!e.danger,c=!!e.prompt;n.classList.toggle("confirm-modal--compact",!!e.compact),i.textContent=e.title??"Confirmation",o.innerHTML=Ht(e.message??""),o.style.whiteSpace="normal",o.hidden=c&&!e.message;let h=()=>{$&&($.hidden=!c||!m?.value)};u&&m&&(c?(u.hidden=!1,E&&(E.textContent=e.promptLabel||"",E.hidden=!e.promptLabel),m.value=e.defaultValue??"",m.placeholder=e.placeholder??"",m.maxLength=e.maxLength||120):(u.hidden=!0,m.value=""),h()),p.textContent=e.okText??e.confirmLabel??"OK",s.textContent=e.cancelText??e.cancelLabel??(typeof v=="function"?v("cancel"):"Cancel"),r&&r.setAttribute("aria-label",s.textContent);let x=!!(d&&e.altText&&!c);d&&(d.hidden=!x,d.textContent=x?e.altText:""),a&&a.setAttribute("data-lucide",_?"trash-2":c?"pencil":"help-circle");let A=document.activeElement,f=()=>{p.removeEventListener("click",T),s.removeEventListener("click",L),d&&d.removeEventListener("click",S),r&&r.removeEventListener("click",L),n.removeEventListener("click",R),document.removeEventListener("keydown",C),m&&(m.removeEventListener("keydown",D),m.removeEventListener("input",F)),$&&$.removeEventListener("click",V)},y=B=>{if(ge===t){ge=null,Fe=null,n.classList.remove("active","confirm-modal--compact"),u&&(u.hidden=!0),o&&(o.hidden=!1),d&&(d.hidden=!0),$&&($.hidden=!0),f(),t(B);try{A&&typeof A.focus=="function"&&A.focus()}catch{}}},T=()=>{if(c){let B=(m?.value??"").trim();if(!B){try{m?.focus()}catch{}return}y(B);return}y(!0)},L=()=>y(c?null:!1),S=()=>y("alt"),R=B=>{B.target===n&&y(c?null:!1)},C=B=>{B.key==="Escape"&&(B.preventDefault(),y(c?null:!1))},D=B=>{B.key==="Enter"&&(B.preventDefault(),T())},F=()=>h(),V=B=>{if(B.preventDefault(),!!m){m.value="",h();try{m.focus()}catch{}}};if(ge=t,Fe=f,p.addEventListener("click",T),s.addEventListener("click",L),x&&d.addEventListener("click",S),r&&r.addEventListener("click",L),n.addEventListener("click",R),document.addEventListener("keydown",C),c&&m&&(m.addEventListener("keydown",D),m.addEventListener("input",F)),c&&$&&$.addEventListener("click",V),n.classList.add("active"),window.lucide)try{a&&a.parentElement?.querySelectorAll("svg").forEach(B=>B.remove()),window.lucide.createIcons({nodes:[n]})}catch{}try{c&&m?(m.focus(),m.select()):p.focus()}catch{}})}function Yt(e={}){return ht({...e,prompt:!0,title:e.title??(typeof window._t=="function"?window._t("new_preset"):"Name"),message:e.message??"",okText:e.okText??(typeof window._t=="function"&&window._t("ok")||"OK"),cancelText:e.cancelText??(typeof window._t=="function"?window._t("cancel"):"Cancel")})}function _t(){window.appConfirm=ht,window.appPrompt=Yt,window.dismissAppConfirm=()=>mt(!1)}function l(e){return String(e??"").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;").replace(/'/g,"&#39;")}function b(e){return l(e).replace(/`/g,"&#96;")}var Xe="default",xe=[{id:"default",label:null,css:"'JetBrains Mono', Consolas, monospace"},{id:"consolas",label:"Consolas",css:"Consolas, monospace"},{id:"cascadia-mono",label:"Cascadia Mono",css:"'Cascadia Mono', Consolas, monospace"},{id:"cascadia-code",label:"Cascadia Code",css:"'Cascadia Code', Consolas, monospace"},{id:"courier-new",label:"Courier New",css:"'Courier New', monospace"},{id:"lucida-console",label:"Lucida Console",css:"'Lucida Console', monospace"},{id:"inter",label:"Inter",css:"'Inter', sans-serif"},{id:"roboto-condensed",label:"Roboto Condensed",css:"'Roboto Condensed', sans-serif"}];function Ge(e){let t=Math.round(Number(e));return Number.isFinite(t)?Math.min(28,Math.max(9,t)):12}function He(e){let t=String(e??"").trim().toLowerCase();return xe.some(n=>n.id===t)?t:Xe}function yt(e){if(e.label)return e.label;let t=typeof window._t=="function"?window._t("config_editor_font_default"):"";return t&&t!=="config_editor_font_default"?t:"Default (JetBrains Mono)"}function $e(e=window.app?.realData?.managerSettings){return{size:Ge(e?.configEditorFontSize??12),family:He(e?.configEditorFontFamily)}}function Be(e,t=$e()){if(!e)return;let n=xe.find(i=>i.id===He(t.family))||xe[0];e.style.setProperty("--cfg-editor-font-size",`${Ge(t.size)}px`),e.style.setProperty("--cfg-editor-font-family",n.css)}var Se=null;function gt(e){let t=Ge(e.size),n=He(e.family),i=window.app?.realData?.managerSettings;i&&(i.configEditorFontSize=t,i.configEditorFontFamily=n),Se&&clearTimeout(Se),Se=setTimeout(()=>{Se=null,window.chrome?.webview?.postMessage({type:"SAVE_CONFIG_EDITOR_FONT",fontSize:t,fontFamily:n})},400)}var we=(e,t)=>{let n=typeof window._t=="function"?window._t(e):"";return n&&n!==e?n:t};function xt(e,{idPrefix:t,getFont:n,onStep:i,onFamily:o,onReset:a,isSuspended:p=()=>!1}){if(!e)return{close(){},destroy(){}};let s=null,d=()=>{s&&(s.remove(),s=null,e.setAttribute("aria-expanded","false"),document.removeEventListener("mousedown",r,!0),document.removeEventListener("keydown",u,!0))},r=E=>{s&&(s.contains(E.target)||e.contains(E.target)||E.target?.closest?.(".themed-select-menu")||d())},u=E=>{if(E.key!=="Escape"||!s||p()||document.querySelector(".themed-select-menu"))return;let v=s.querySelector(`#${t}-hint`);if(v&&!v.hidden){E.preventDefault(),E.stopPropagation(),v.hidden=!0;let _=s.querySelector(`#${t}-info`);_?.setAttribute("aria-expanded","false"),_?.classList.remove("active");return}E.preventDefault(),E.stopPropagation(),d(),e.focus()},m=()=>{let E=n();s=document.createElement("div"),s.className="config-font-popover",s.setAttribute("role","dialog"),s.setAttribute("aria-label",we("config_editor_font","Editor font"));let v=we("config_editor_font_hint","Tip: Ctrl + Mouse Wheel in the editor changes the size. Ctrl + 0 resets it.");s.innerHTML=`
            <div class="config-font-head">
                <h3>${l(we("config_editor_font","Editor font"))}</h3>
                <div class="config-font-info-wrap">
                    <button type="button" class="config-font-info" id="${t}-info" aria-expanded="false" aria-controls="${t}-hint" aria-label="${b(v)}">
                        <i data-lucide="info"></i>
                    </button>
                    <div class="config-font-hint" id="${t}-hint" role="tooltip" hidden>${l(v)}</div>
                </div>
            </div>
            <span class="config-font-label">${l(we("config_editor_font_size_label","Font size"))}</span>
            <div class="config-font-stepper">
                <button type="button" data-font-step="-1" aria-label="-"><i data-lucide="minus"></i></button>
                <span id="${t}-size-value" class="config-font-value">${E.size}px</span>
                <button type="button" data-font-step="1" aria-label="+"><i data-lucide="plus"></i></button>
            </div>
            <label class="config-font-label" for="${t}-family">${l(we("config_editor_font_family_label","Font"))}</label>
            <select id="${t}-family" class="tweak-select">
                ${xe.map(f=>`<option value="${b(f.id)}" ${f.id===E.family?"selected":""}>${l(yt(f))}</option>`).join("")}
            </select>
            <div class="config-font-actions">
                <button type="button" class="btn-popup secondary" id="${t}-reset">${l(we("config_editor_font_reset","Reset"))}</button>
            </div>`,e.parentElement.appendChild(s);let _=e.getBoundingClientRect(),c=e.parentElement.getBoundingClientRect();s.style.top=`${_.bottom-c.top+6}px`,s.style.right=`${Math.max(0,c.right-_.right)}px`;try{window.lucide?.createIcons({nodes:[s]})}catch{}let h=s.querySelector(`#${t}-info`),x=s.querySelector(`#${t}-hint`),A=f=>{!x||!h||(x.hidden=!f,h.setAttribute("aria-expanded",String(f)),h.classList.toggle("active",f))};h?.addEventListener("click",f=>{f.stopPropagation(),A(!!x?.hidden)}),s.addEventListener("click",f=>{!x||x.hidden||f.target.closest(`#${t}-info, #${t}-hint`)||A(!1)}),s.querySelectorAll("[data-font-step]").forEach(f=>{f.addEventListener("click",()=>i(Number(f.dataset.fontStep)))}),s.querySelector(`#${t}-family`)?.addEventListener("change",f=>o(f.target.value)),s.querySelector(`#${t}-reset`)?.addEventListener("click",()=>a()),e.setAttribute("aria-expanded","true"),document.addEventListener("mousedown",r,!0),document.addEventListener("keydown",u,!0)},$=()=>{s?d():m()};return e.addEventListener("click",$),{close:d,destroy(){d(),e.removeEventListener("click",$)}}}var Et=Math.floor(Math.random()*1e9);function Tt(e){return e==="missing_edit"?window._t("bundle_preview_edit_missing"):e==="missing_save"?window._t("bundle_preview_save_missing"):e==="outside_workspace"?window._t("bundle_preview_edit_outside"):e?window._t("bundle_preview_edit_failed",e):""}function Re({idPrefix:e,name:t,meta:n,text:i,truncated:o,showPopout:a}){return`
        <div class="ba2-text-view" data-ba2-text-view>
            <div class="ba2-preview-header ba2-text-header">
                <div class="ba2-text-title">
                    <div class="ba2-preview-name">${l(t)}</div>
                    ${n?`<div class="ba2-preview-meta">${l(n)}</div>`:""}
                </div>
                <div class="ba2-text-tools">
                    <span class="ba2-text-dirty" data-text-dirty hidden title="${b(window._t("bundle_preview_unsaved"))}"></span>
                    <button type="button" class="ba2-preview-tool ba2-preview-tool--primary" data-text-action="save" disabled title="${b(window._t("config_editor_save_tooltip"))}">
                        <i data-lucide="save"></i><span>${l(window._t("bundle_preview_save"))}</span>
                    </button>
                    <button type="button" class="ba2-preview-tool" data-text-action="discard" hidden>
                        <span>${l(window._t("bundle_preview_discard"))}</span>
                    </button>
                    <div class="ba2-text-font-wrap">
                        <button type="button" class="ba2-preview-tool ba2-preview-tool--icon" id="${e}-btn" aria-haspopup="dialog" aria-expanded="false" title="${b(window._t("config_editor_font"))}">
                            <i data-lucide="type"></i>
                        </button>
                    </div>
                    ${a?`
                    <button type="button" class="ba2-preview-tool ba2-preview-tool--icon" data-preview-popout title="${b(window._t("bundle_preview_popout"))}">
                        <i data-lucide="external-link"></i>
                    </button>`:""}
                </div>
            </div>
            ${o?`<div class="ba2-preview-note" data-text-truncated>${l(window._t("preview_truncated"))}</div>`:""}
            <div class="ba2-text-body">
                <textarea class="ba2-text-editor" data-text-edit spellcheck="false" wrap="off" readonly>${l(i||"")}</textarea>
            </div>
        </div>
    `}function kt(e,{idPrefix:t,target:n,post:i,confirm:o,onDirtyChange:a,onStatus:p,onPopout:s,initialEdit:d=null}){let r=e?.querySelector("[data-ba2-text-view]");if(!r)return null;let u=r.querySelector("[data-text-edit]"),m=r.querySelector("[data-text-dirty]"),$=r.querySelector('[data-text-action="save"]'),E=r.querySelector('[data-text-action="discard"]'),v=r.querySelector("[data-text-truncated]"),_=!1,c=!1,h=!1,x=!1,A="",f="utf8",y="lf",T=0,L=0,S=!1,R=$e();Be(r,R);let C=()=>{m&&(m.hidden=!S),$&&($.disabled=!h||x||c||!S),E&&(E.hidden=!S),u&&(u.readOnly=!h||x||_)},D=g=>{S!==g&&(S=g,C(),a?.(S))},F=()=>{_||!n||h||x||(_=!0,C(),T=++Et,i({type:"BUNDLE_LOAD_TEXT_EDIT",source:n.source,mode:n.mode,path:n.path,requestId:T}))},V=()=>{!h||x||c||!S||!n||!u||(c=!0,C(),L=++Et,i({type:"BUNDLE_SAVE_TEXT_EDIT",archive:n.archive,path:n.entryPath,text:u.value,encoding:f,eol:y,requestId:L}))},B=async()=>{!S||!u||o&&!await o(window._t("bundle_preview_unsaved_confirm"))||(u.value=A,D(!1))},O=g=>{let P=R;R=$e({configEditorFontSize:g.size,configEditorFontFamily:g.family}),Be(r,R),(P.size!==R.size||P.family!==R.family)&&gt(R);let K=document.getElementById(`${t}-size-value`);K&&(K.textContent=`${R.size}px`)},Q=g=>{let P=g===0?12:R.size+g;O({...R,size:P})},X=xt(r.querySelector(`#${t}-btn`),{idPrefix:t,getFont:()=>R,onStep:Q,onFamily:g=>O({...R,family:g}),onReset:()=>O({size:12,family:Xe})}),ne=g=>{g.ctrlKey&&(g.preventDefault(),g.deltaY&&Q(g.deltaY<0?1:-1))},H=g=>{if(!(g.ctrlKey||g.metaKey)||g.altKey||!r.contains(document.activeElement))return;let P=g.key;if(P==="s"||P==="S"){g.preventDefault(),g.stopPropagation(),V();return}let K=null;P==="="||P==="+"?K=1:P==="-"||P==="_"?K=-1:P==="0"&&(K=0),K!==null&&(g.preventDefault(),g.stopPropagation(),Q(K))},oe=()=>D(u.value!==A),pe=g=>{if(g.key!=="Tab"||g.ctrlKey||g.altKey||g.metaKey)return;g.preventDefault();let P=u.selectionStart,K=u.selectionEnd;u.setRangeText("	",P,K,"end"),oe()},fe=r.querySelector(".ba2-text-body");if(fe?.addEventListener("wheel",ne,{passive:!1}),document.addEventListener("keydown",H,!0),u?.addEventListener("input",oe),u?.addEventListener("keydown",pe),$?.addEventListener("click",V),E?.addEventListener("click",()=>{B()}),r.querySelector("[data-preview-popout]")?.addEventListener("click",()=>s?.()),d&&u){A=d.original,f=d.encoding,y=d.eol,h=!0,u.value=d.value,u.readOnly=!1,v&&(v.hidden=!0),C(),oe(),u.scrollTop=d.scrollTop||0;let g=d.selectionStart??0,P=d.selectionEnd??g;try{u.setSelectionRange(g,P)}catch{}}else C(),F();return{isDirty:()=>S,isEditing:()=>h,getEditState:()=>h&&u?{original:A,value:u.value,encoding:f,eol:y,scrollTop:u.scrollTop,selectionStart:u.selectionStart,selectionEnd:u.selectionEnd}:null,applyFont(g){R=$e({configEditorFontSize:g.size,configEditorFontFamily:g.family}),Be(r,R);let P=document.getElementById(`${t}-size-value`);P&&(P.textContent=`${R.size}px`)},handleMessage(g){if(g?.type==="BUNDLE_TEXT_EDIT_CONTENT"&&T&&g.requestId===T){if(T=0,_=!1,g.error)return g.error==="too_large"?(x=!0,v&&(v.hidden=!1,v.textContent=window._t("bundle_preview_edit_too_large")),C(),!0):(C(),p?.("error",Tt(g.error)),!0);if(A=g.text||"",f=g.encoding||"utf8",y=g.eol||"lf",h=!0,u){u.value=A,u.scrollTop=0;try{u.setSelectionRange(0,0)}catch{}}return v&&(v.hidden=!0),D(!1),C(),!0}return(g?.type==="BUNDLE_TEXT_EDIT_SAVED"||g?.type==="BUNDLE_TEXT_EDIT_SAVE_FAILED")&&L&&g.requestId===L?(L=0,c=!1,g.type==="BUNDLE_TEXT_EDIT_SAVE_FAILED"?(C(),p?.("error",Tt(g.error)),!0):(A=u.value,D(!1),C(),!0)):!1},destroy(){X.destroy(),fe?.removeEventListener("wheel",ne),document.removeEventListener("keydown",H,!0)}}}function Rt(e){return e==="unsupported"?window._t("preview_unsupported_format"):e==="hkx_behavior"?window._t("preview_hkx_behavior"):e==="hkx_character"?window._t("preview_hkx_character"):e==="hkx_project"?window._t("preview_hkx_project"):e==="hkx_not_clip"?window._t("preview_hkx_not_clip"):e==="hkx_no_skeleton"?window._t("preview_hkx_no_skeleton"):window._t("preview_unavailable")}function j(e){let t=Number(e)||0;return t<1024?`${t} B`:t<1024*1024?`${(t/1024).toFixed(1)} KB`:`${(t/(1024*1024)).toFixed(1)} MB`}function Jt(e,t,n,i){let o=n?e.getOverlaySourceSize(n):void 0;return o?j(o):i&&!t.size?"\u2014":j(t.size)}function Qe(e){return String(e||"").replace(/\\/g,"/").toLowerCase()}function Ct(e){let t=e.files.reduce((n,i)=>n+(i.added?0:1),0);for(let n of Object.keys(e.folders))t+=Ct(e.folders[n]);return e.totalFiles=t,t}function Pt(e,t){let n=0;for(let i of e.files)i.added||t.has(Qe(i.fullPath))&&n++;for(let i of Object.keys(e.folders))n+=Pt(e.folders[i],t);return n}function Ye(e,t){return e?t===0?{checked:!1,indeterminate:!1}:t===e?{checked:!0,indeterminate:!1}:{checked:!1,indeterminate:!0}:{checked:!1,indeterminate:!1}}function Zt(e){return e.getStagedBa2Sources().length===0&&e.selectedFolders.size>0&&e.selectedMods.size===0&&e.externalFiles.size===0?window._t("file_entry_folders_only"):window._t("file_entry_expand_ba2")}function Qt(e,t){let n=e.normalizeArchiveKey(t),{replaced:i,added:o}=e.getOverlayCounts(n),a="";return i>0&&(a+=`<div class="staged-partial-badge staged-replaced-badge">${l(window._t("bundle_replaced_count",i))}</div>`),o>0&&(a+=`<div class="staged-partial-badge staged-replaced-badge staged-added-badge">${l(window._t("bundle_added_count",o))}</div>`),a}function At(e,t){let n=e.normalizeArchiveKey(t),i=Qt(e,t),o=e.ba2TotalPaths.get(n);if(!o)return i;let a=e.getSelectedCount(n);return a>=o?i:`<div class="staged-partial-badge">${l(window._t("staged_partial_files",a,o))}</div>${i}`}function et(e){let t={folders:{},files:[]};for(let n of e){let i=String(n.path||""),o=i.split("/").filter(Boolean);if(o.length===0)continue;let a=t;for(let p=0;p<o.length-1;p++){let s=o[p];a.folders[s]||(a.folders[s]={folders:{},files:[]}),a=a.folders[s]}a.files.push({name:o[o.length-1],size:n.unpackedSize,fullPath:i,added:!!n.added})}return Ct(t),t}function tt(e,t){return t?e.toLowerCase().includes(t.toLowerCase()):!0}function it(e,t,n,i=0,o=!1){let a=n.inspectorFilter||"",p=n.inspectorExpandedDirs||new Set,s=o?"":n.getArchiveSelectionKey(),d=o?new Set:n.getSelectionForArchive(s),r=Object.keys(e.folders).sort((v,_)=>v.localeCompare(_)),u=[...e.files].sort((v,_)=>v.name.localeCompare(_.name)),m=8+i*14,$="";for(let v of r){let _=e.folders[v],c=t?`${t}/${v}`:v,h=p.has(c),x="";if((h||a)&&(x=it(_,c,n,i+1,o),!x&&a))continue;let A=_.totalFiles??0,f=Pt(_,d),y=Ye(A,f),T=o?"":`
                    <input type="checkbox" class="ba2-tree-check ba2-tree-folder-check"
                        data-check-type="folder"
                        data-folder-path="${b(c)}"
                        ${y.checked?"checked":""}
                        ${y.indeterminate?'data-indeterminate="true"':""}>`;$+=`
            <div class="ba2-tree-folder${h?" ba2-tree-folder--expanded":""}" data-folder-path="${b(c)}">
                <div class="ba2-tree-row ba2-tree-folder-row${o?" ba2-tree-row--readonly":""}" style="padding-left: ${m}px">
                    ${T}
                    <button type="button" class="ba2-tree-folder-btn" data-folder-path="${b(c)}">
                        <i data-lucide="chevron-right" class="ba2-tree-chevron"></i>
                        <i data-lucide="folder" class="ba2-tree-folder-icon"></i>
                        <span>${l(v)}</span>
                    </button>
                </div>
                ${h?`<div class="ba2-tree-children">${x}</div>`:""}
            </div>
        `}let E=!o&&n.inspectedBa2Path&&n.selectedFolders.size>0?n.getFolderOverrides():null;for(let v of u){if(!tt(v.fullPath,a))continue;let _=Qe(v.fullPath),c=o?"":n.getReplacement(s,_),h=!!v.added||!!c&&n.isOverlayAddition(s,_),x=!c&&!h&&E&&E.get(_)||"",A=d.has(_)||!!c||h,f=o?"":`
                <input type="checkbox" class="ba2-tree-check ba2-tree-file-check"
                    data-check-type="file"
                    data-file-path="${b(_)}"
                    ${A?"checked":""}
                    ${c||h?"disabled":""}>`,y=!o&&n.previewPath===_,T="",L="";h&&c?(T=window._t("bundle_added_from",c),L=window._t("bundle_added_badge")):c?(T=window._t("bundle_replaced_from",c),L=window._t("bundle_replaced_badge")):x&&(T=window._t("bundle_overridden_by_folder",x),L=window._t("bundle_replaced_badge"));let S=T?`<span class="ba2-tree-replaced-badge${h?" ba2-tree-added-badge":""}" title="${b(T)}">${l(L)}</span>`:"";$+=`
            <div class="ba2-tree-row ba2-tree-file${o?" ba2-tree-row--readonly":""}${y?" ba2-tree-file--previewing":""}${T?" ba2-tree-file--replaced":""}${h?" ba2-tree-file--added":""}" data-preview-path="${b(_)}" style="padding-left: ${m}px">
                ${f}
                <i data-lucide="${ii(v.name)}" class="ba2-tree-file-icon"></i>
                <span class="ba2-tree-file-name">${l(v.name)}</span>
                ${S}
                <span class="ba2-tree-file-size">${l(Jt(n,v,c,h))}</span>
            </div>
        `}return $}function ei(e){let t=e.inspectedBa2Path?e.normalizeArchiveKey(e.inspectedBa2Path):"",n="";for(let o of e.selectedMods){let a=e.resolveArchivePath(o),p=e.normalizeArchiveKey(a);if(t&&p===t&&e.inspectedBa2Path)continue;let s=t&&p===t?" file-entry-staged-item--active":"",d=At(e,a);n+=`
            <div class="file-entry-staged-item file-entry-staged-ba2 bundle-staged-item bundle-staged-item--internal staged-item staged-item-internal${s}" data-path="${b(a)}">
                <i data-lucide="package" class="bundle-staged-icon"></i>
                <div class="bundle-staged-item-body">
                    <div class="bundle-staged-item-name">${l(o.split("/").pop())}</div>
                    <div class="bundle-staged-item-meta">${window._t("internal_mod")}</div>
                    ${d}
                </div>
                <button class="btn-icon small btn-remove-staged" data-name="${b(o)}" data-type="internal" title="${b(window._t("remove_button"))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `}for(let o of e.externalFiles){let a=e.normalizeArchiveKey(o);if(t&&a===t&&e.inspectedBa2Path)continue;let p=t&&a===t?" file-entry-staged-item--active":"",s=At(e,o);n+=`
            <div class="file-entry-staged-item file-entry-staged-ba2 bundle-staged-item bundle-staged-item--external staged-item staged-item-external${p}" data-path="${b(o)}">
                <i data-lucide="file-archive" class="bundle-staged-icon"></i>
                <div class="bundle-staged-item-body">
                    <div class="bundle-staged-item-name">${l(o.split(/[\\/]/).pop())}</div>
                    <div class="bundle-staged-item-meta bundle-staged-item-meta--external">${l(o)}</div>
                    ${s}
                </div>
                <button class="btn-icon small btn-remove-staged" data-name="${b(o)}" data-type="external" title="${b(window._t("remove_button"))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `}let i=e.inspectedFolderPath?e.normalizeFolderKey(e.inspectedFolderPath):"";for(let o of e.selectedFolders){let a=e.normalizeFolderKey(o);if(i&&a===i&&e.inspectedFolderPath)continue;let p=i&&a===i?" file-entry-staged-item--active":"",s=e.ba2TotalPaths.get(a),d="";if(s){let r=e.getSelectedCount(a);r<s&&(d=`<div class="staged-partial-badge">${l(window._t("staged_partial_files",r,s))}</div>`)}n+=`
            <div class="file-entry-staged-item file-entry-staged-folder bundle-staged-item bundle-staged-item--folder staged-item${p}" data-path="${b(o)}">
                <i data-lucide="folder" class="bundle-staged-icon"></i>
                <div class="bundle-staged-item-body">
                    <div class="bundle-staged-item-name">${l(o.split(/[\\/]/).pop()||o)}</div>
                    <div class="bundle-staged-item-meta">${window._t("staged_folder")}</div>
                    ${d}
                </div>
                <button class="btn-icon small btn-remove-staged" data-name="${b(o)}" data-type="folder" title="${b(window._t("remove_button"))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `}return n}function Je(e){let t=e.getArchiveSelectionKey();if(!t)return"";let n=!!e.inspectorFilter,i=n?e.getFilteredSelectedCount(t):e.getSelectedCount(t),o=n?e.getFilteredEntryCount():e.ba2TotalPaths.get(t)||e.inspectorFileCount||0;return o?n?window._t("inspector_selected_count_filtered",i,o):window._t("inspector_selected_count",i,o):""}function ti(e){return It(e,{unified:!0})}function Lt(e){let t=e.inspectedFolderPath||(e.inspectedBa2Path?e.inspectedResolvedPath||e.resolveArchivePath(e.inspectedBa2Path):"");return t?`<div class="bundle-path-bar" title="${b(t)}">${l(t)}</div>`:""}function Ft(e){if(e.inspectedFolderPath){let d=b(e.inspectorFilter||""),r=Je(e);return`
        <div class="bundle-inspector-pathbar">
            <div class="search-box bundle-inspector-search-wrap">
                <i data-lucide="search"></i>
                <input type="text" id="ba2-inspector-filter" class="bundle-field" placeholder="${l(window._t("inspector_search_placeholder"))}" value="${d}">
                <button type="button" class="search-clear" id="ba2-inspector-filter-clear" title="${b(window._t("clear_search"))}" aria-label="${b(window._t("clear_search"))}" ${e.inspectorFilter?"":"hidden"}>
                    <i data-lucide="x"></i>
                </button>
            </div>
            <div class="bundle-inspector-pathbar-meta">
                <span id="ba2-inspector-count">${l(r)}</span>
            </div>
            <div class="bundle-inspector-pathbar-actions">
                <button type="button" class="bundle-text-btn" id="btn-inspector-select-all">${window._t("inspector_select_all")}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-deselect-all">${window._t("inspector_deselect_all")}</button>
            </div>
        </div>
    `}let t=e.getStagedBa2Sources(),n=t.map(d=>`
        <option value="${b(d.inspectPath)}" ${e.normalizeArchiveKey(d.inspectPath)===e.normalizeArchiveKey(e.inspectedBa2Path)?"selected":""}>${l(d.name)}</option>
    `).join(""),i=b(e.inspectorFilter||""),o=e.getArchiveSelectionKey(),a=o&&e.getEffectiveSelectionPaths(o).length>0,p=Je(e);return`
        <div class="bundle-inspector-pathbar">
            ${t.length>=2?`
                <select id="bundle-staged-ba2-select" class="bundle-field bundle-staged-ba2-select" title="${b(window._t("staged_items",t.length))}">
                    ${n}
                </select>
            `:""}
            <div class="search-box bundle-inspector-search-wrap">
                <i data-lucide="search"></i>
                <input type="text" id="ba2-inspector-filter" class="bundle-field" placeholder="${l(window._t("inspector_search_placeholder"))}" value="${i}">
                <button type="button" class="search-clear" id="ba2-inspector-filter-clear" title="${b(window._t("clear_search"))}" aria-label="${b(window._t("clear_search"))}" ${e.inspectorFilter?"":"hidden"}>
                    <i data-lucide="x"></i>
                </button>
            </div>
            <div class="bundle-inspector-pathbar-meta">
                ${e.inspectorArchiveType?`<span>${l(e.inspectorArchiveType)}</span>`:""}
                <span id="ba2-inspector-count">${l(p)}</span>
            </div>
            <div class="bundle-inspector-pathbar-actions">
                <button type="button" class="bundle-text-btn" id="btn-inspector-select-all">${window._t("inspector_select_all")}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-deselect-all">${window._t("inspector_deselect_all")}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-isolate" ${a?"":"disabled"}>${window._t("bundle_add_to_bundle")}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-extract" ${a?"":"disabled"}>${window._t("bundle_extract_to_folder")}</button>
            </div>
        </div>
    `}function It(e,{unified:t=!1}={}){let n=!!e.inspectedFolderPath,i=n?e.inspectedFolderPath:e.inspectedBa2Path;if(e.inspectorLoading)return`<div class="ba2-explorer-empty">${l(window._t("inspector_loading"))}</div>`;if(!i)return t?"":`<div class="ba2-explorer-empty">${l(Zt(e))}</div>`;let o=n?"":e.getArchiveSelectionKey(),a=!n&&o?e.getOverlayAdditions(o):[];if((!e.ba2Contents||e.ba2Contents.length===0)&&a.length===0){let c=n?window._t("inspector_folder_empty"):window._t("no_ba2_selected");return`<div class="ba2-explorer-empty">${l(c)}</div>`}let p=e.inspectorFilter||"",s=Array.isArray(e.ba2Contents)?[...e.ba2Contents]:[];if(a.length>0){let c=new Set(s.map(h=>Qe(h.path)));for(let{path:h}of a)c.has(h)||(s.push({path:h,unpackedSize:0,added:!0}),c.add(h))}if(p&&(s=s.filter(c=>tt(String(c.path||""),p))),s.length===0)return`<div class="ba2-explorer-empty">${l(window._t("inspector_no_matches"))}</div>`;let d=et(s),r=it(d,"",e,1,!1);if(!r)return`<div class="ba2-explorer-empty">${l(window._t("inspector_no_matches"))}</div>`;let u=i.split(/[\\/]/).pop();if(n){let c=e.getArchiveSelectionKey(),h=!!e.inspectorFilter,x=h?e.getFilteredEntryCount():e.ba2TotalPaths.get(c)||s.length,A=h?e.getFilteredSelectedCount(c):e.getSelectedCount(c),f=Ye(x,A);return`
            <div class="ba2-file-entry ba2-file-entry--unified ba2-file-entry--folder">
                <div class="ba2-tree-row ba2-tree-archive-root" style="padding-left: 8px">
                    <input type="checkbox" class="ba2-tree-check ba2-tree-archive-root-check"
                        ${f.checked?"checked":""}
                        ${f.indeterminate?'data-indeterminate="true"':""}>
                    <i data-lucide="folder" class="ba2-tree-archive-icon"></i>
                    <span class="ba2-tree-archive-name">${l(u)}</span>
                </div>
                <div class="ba2-tree-children">${r}</div>
            </div>
        `}let m=e.getArchiveSelectionKey(),$=!!e.inspectorFilter,E=$?e.getFilteredEntryCount():e.ba2TotalPaths.get(m)||s.length,v=$?e.getFilteredSelectedCount(m):e.getSelectedCount(m),_=Ye(E,v);return t?`
            <div class="ba2-file-entry ba2-file-entry--unified">
                <div class="ba2-tree-row ba2-tree-archive-root" style="padding-left: 8px">
                    <input type="checkbox" class="ba2-tree-check ba2-tree-archive-root-check"
                        ${_.checked?"checked":""}
                        ${_.indeterminate?'data-indeterminate="true"':""}>
                    <i data-lucide="file-archive" class="ba2-tree-archive-icon"></i>
                    <span class="ba2-tree-archive-name">${l(u)}</span>
                </div>
                <div class="ba2-tree-children">${r}</div>
            </div>
        `:`
        <div class="ba2-file-entry">
            <div class="ba2-tree-row ba2-tree-archive-root" style="padding-left: 8px">
                <input type="checkbox" class="ba2-tree-check ba2-tree-archive-root-check"
                    ${_.checked?"checked":""}
                    ${_.indeterminate?'data-indeterminate="true"':""}>
                <i data-lucide="file-archive" class="ba2-tree-archive-icon"></i>
                <span class="ba2-tree-archive-name">${l(u)}</span>
            </div>
            <div class="ba2-tree-children">${r}</div>
        </div>
    `}function St(e){let t=e.selectedMods.size+e.externalFiles.size+e.selectedFolders.size,n=e.getStagedBa2Sources();if(t===0)return`
            <div class="file-entry-unified file-entry-unified--empty">
                <div class="file-entry-empty">
                    <i data-lucide="package-plus"></i>
                    <div class="file-entry-empty-title">${window._t("workspace_empty")}</div>
                    <div class="file-entry-empty-hint">${window._t("file_entry_drop_hint")}</div>
                </div>
            </div>
        `;let i=!!(e.inspectedBa2Path||e.inspectedFolderPath),o="";i?o=`
            <div class="ba2-inspector-split">
                <div class="ba2-explorer-tree">${It(e,{unified:!0})}</div>
                ${Dt(e)}
            </div>
            <div id="ba2-audio-dock-slot">${Ut(e)}</div>
        `:n.length>0?o=`
            <div class="file-entry-hint">${l(window._t("file_entry_expand_ba2"))}</div>
        `:e.selectedFolders.size>0?o=`
            <div class="file-entry-hint">${l(window._t("file_entry_expand_folder"))}</div>
        `:o=`
            <div class="file-entry-hint">${l(window._t("file_entry_folders_only"))}</div>
        `;let a=ei(e);return`
        <div class="file-entry-unified">
            ${a?`<div class="file-entry-staged-roots">${a}</div>`:""}
            <div class="file-entry-inspect">${o}</div>
        </div>
    `}function me(e){let t=String(e||"").split("/");return t[t.length-1]||e}var Mt=new Set(["mp3","wav","ogg","flac","xwm","fuz"]);function ii(e){let t=String(e||"").split(".").pop().toLowerCase();return Mt.has(t)?"file-audio":"file"}function Ee(e){let t=Math.max(0,Math.floor(Number(e)||0)),n=Math.floor(t/60),i=t%60;return`${n}:${String(i).padStart(2,"0")}`}function Ze(e){return e?e>=1e3?`${(e/1e3).toFixed(e%1e3===0?0:1)} kHz`:`${e} Hz`:""}function Dt(e){let t=!!(e.inspectedBa2Path&&e.previewPath),n=e.previewResult?.kind==="audio",i=t&&!n?`<button type="button" class="bundle-text-btn ba2-preview-extract" id="btn-preview-extract">${l(window._t("preview_extract_file"))}</button>`:"",o;if(!e.previewPath)o=`<div class="ba2-preview-empty">${l(window._t("preview_hint"))}</div>`;else if(e.previewLoading)o=`
            <div class="ba2-preview-header">
                <div class="ba2-preview-name">${l(me(e.previewPath))}</div>
            </div>
            <div class="ba2-preview-empty">${l(window._t("preview_loading"))}</div>
        `;else if(e.previewResult?.kind==="image"&&e.previewResult.dataUrl){let r=e.previewResult,u=[];r.width&&r.height&&u.push(`${r.width}\xD7${r.height}`),r.format&&u.push(r.format),r.mips&&u.push(window._t("preview_mips",r.mips)),r.size&&u.push(j(r.size));let m=r.width&&r.height&&Math.max(r.width,r.height)<=256;o=`
            <div class="ba2-preview-header">
                <div class="ba2-preview-name">${l(me(e.previewPath))}</div>
                <div class="ba2-preview-meta">${l(u.join(" \xB7 "))}</div>
            </div>
            <div class="ba2-preview-stage${m?" ba2-preview-stage--small":""}"><img alt="" src="${b(r.dataUrl)}"></div>
        `}else if(e.previewResult?.kind==="audio")o=ni(e,e.previewResult);else if(e.previewResult?.kind==="material")o=ai(e,e.previewResult);else if(e.previewResult?.kind==="hex")o=si(e,e.previewResult);else if(e.previewResult?.kind==="mesh")o=zt(e,e.previewResult);else if(e.previewResult?.kind==="text"){let r=e.previewResult;o=Re({idPrefix:"ba2-text-font",name:me(e.previewPath),meta:j(r.size),text:r.text,truncated:r.truncated,showPopout:!0})}else{let r=e.previewResult,u=[];r?.format&&u.push(r.format),r?.width&&r?.height&&u.push(`${r.width}\xD7${r.height}`),r?.size&&u.push(j(r.size));let m=Rt(r?.error);o=`
            <div class="ba2-preview-header">
                <div class="ba2-preview-name">${l(me(e.previewPath))}</div>
                ${u.length?`<div class="ba2-preview-meta">${l(u.join(" \xB7 "))}</div>`:""}
            </div>
            <div class="ba2-preview-empty">${l(m)}</div>
        `}let a=n||e.previewLoading&&Nt(e.previewPath),p=!e.previewPath||a&&!e._audioInfoOpen?" ba2-preview-pane--hidden":"",d=!e.previewLoading&&["mesh","image"].includes(e.previewResult?.kind)?`<button type="button" class="bundle-text-btn ba2-preview-popout" data-preview-popout>${l(window._t("bundle_preview_popout"))}</button>`:"";return`
        <aside class="ba2-preview-pane${p}">
            ${o}
            ${d||i?`<div class="ba2-preview-footer">${d}${i}</div>`:""}
        </aside>
    `}function ni(e,t){let n=t.audio||{},i=[];n.codec&&i.push(n.codec),n.sampleRate&&i.push(Ze(n.sampleRate)),n.channels===1?i.push(window._t("preview_audio_mono")):n.channels===2?i.push(window._t("preview_audio_stereo")):n.channels&&i.push(window._t("preview_audio_channels",n.channels)),n.duration&&i.push(Ee(n.duration)),t.size&&i.push(j(t.size));let o=[];n.codec&&o.push([window._t("preview_audio_codec"),n.codec]),n.sampleRate&&o.push([window._t("preview_audio_sample_rate"),Ze(n.sampleRate)]),n.channels&&o.push([window._t("preview_audio_channels_label"),n.channels===1?window._t("preview_audio_mono"):n.channels===2?window._t("preview_audio_stereo"):String(n.channels)]),n.duration&&o.push([window._t("preview_audio_duration"),Ee(n.duration)]),t.size&&o.push([window._t("preview_audio_size"),j(t.size)]);let a=o.map(([s,d])=>`
            <div class="ba2-audio-row">
                <span class="ba2-audio-label">${l(s)}</span>
                <span class="ba2-audio-value">${l(String(d))}</span>
            </div>`).join(""),p=n.lipBytes?`<div class="ba2-preview-note">${l(window._t("preview_audio_lip",j(n.lipBytes)))}</div>`:"";return`
        ${Te(e,i.join(" \xB7 "))}
        ${a?`<div class="ba2-audio-info">${a}</div>`:""}
        ${p}
        <div class="ba2-preview-audio-hint">${l(window._t("preview_audio_space_hint"))}</div>
    `}function Nt(e){let t=String(e||"").split(".").pop().toLowerCase();return Mt.has(t)}function oi(e,t){let n=[],i=String(e.previewPath||"").split("/").filter(Boolean);return i.length>1&&n.push(i[i.length-2]),t.codec&&n.push(t.codec),t.sampleRate&&n.push(Ze(t.sampleRate)),t.channels===1?n.push(window._t("preview_audio_mono")):t.channels===2?n.push(window._t("preview_audio_stereo")):t.channels&&n.push(window._t("preview_audio_channels",t.channels)),t.silent&&n.push(window._t("preview_audio_silent_label")),n.join(" \u2022 ")}function Ut(e){let t=e.previewResult?.kind==="audio",n=!t&&e.previewLoading&&Nt(e.previewPath);if(!t&&!n)return"";let i=t?e.previewResult:{},o=i.audio||{},a=i.audioUrl?`https://f76manager.app/${i.audioUrl}`:"",p=o.duration?Ee(o.duration):"0:00",s=Math.round((e._audioVolume??1)*100),d=window._t("preview_audio_play"),r=n?window._t("preview_loading"):oi(e,o),u=n?"disabled":"";return`
        <div class="ba2-audio-dock${n?" ba2-audio-dock--loading":""}">
            ${t?`<audio preload="metadata" class="ba2-preview-audio-hidden" id="ba2-preview-audio"
                data-audio-url="${b(a)}"></audio>`:""}
            <input type="range" class="ba2-audio-slider ba2-audio-seek" id="ba2-audio-seek"
                min="0" max="10000" value="0" step="1" style="--value: 0%" ${u}
                title="${b(window._t("preview_audio_seek"))}" aria-label="${b(window._t("preview_audio_seek"))}">
            <div class="ba2-audio-player" id="ba2-audio-player">
                <div class="ba2-audio-dock-info">
                    <div class="ba2-audio-dock-art"><i data-lucide="audio-lines"></i></div>
                    <div class="ba2-audio-dock-text">
                        <div class="ba2-audio-dock-name" title="${b(e.previewPath||"")}">${l(me(e.previewPath))}</div>
                        <div class="ba2-audio-dock-sub">${l(r)}</div>
                    </div>
                </div>
                <div class="ba2-audio-dock-controls">
                    <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-prev"
                        title="${b(window._t("preview_audio_prev"))}" aria-label="${b(window._t("preview_audio_prev"))}">
                        <i data-lucide="skip-back"></i>
                    </button>
                    <button type="button" class="ba2-audio-play" id="ba2-audio-play" ${u}
                        title="${b(window._t("preview_shortcut_space",d))}" aria-label="${b(d)}">
                        <span id="ba2-audio-icon-play"><i data-lucide="play"></i></span>
                        <span id="ba2-audio-icon-pause" hidden><i data-lucide="pause"></i></span>
                    </button>
                    <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-next"
                        title="${b(window._t("preview_audio_next"))}" aria-label="${b(window._t("preview_audio_next"))}">
                        <i data-lucide="skip-forward"></i>
                    </button>
                </div>
                <div class="ba2-audio-dock-right">
                    ${e.inspectedBa2Path?`<button type="button" class="ba2-audio-extract" id="ba2-audio-extract" ${u}
                        title="${b(window._t("preview_extract_file"))}" aria-label="${b(window._t("preview_extract_file"))}">
                        <i data-lucide="download"></i><span class="ba2-audio-extract-label">${l(window._t("preview_extract_file"))}</span>
                    </button>`:""}
                    <span class="ba2-audio-time" id="ba2-audio-time">0:00 / ${l(p)}</span>
                    <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-loop" ${u}
                        title="${b(window._t("preview_audio_loop"))}" aria-label="${b(window._t("preview_audio_loop"))}" aria-pressed="false">
                        <i data-lucide="repeat"></i>
                    </button>
                    <button type="button" class="ba2-audio-icon-btn${e._audioInfoOpen?" ba2-audio-icon-btn--on":""}" id="ba2-audio-info"
                        title="${b(window._t("preview_audio_info"))}" aria-label="${b(window._t("preview_audio_info"))}" aria-pressed="${e._audioInfoOpen?"true":"false"}">
                        <i data-lucide="info"></i>
                    </button>
                    <div class="ba2-audio-volume-group">
                        <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-mute"
                            title="${b(window._t("preview_audio_mute"))}" aria-label="${b(window._t("preview_audio_mute"))}">
                            <span id="ba2-audio-icon-volume"><i data-lucide="volume-2"></i></span>
                            <span id="ba2-audio-icon-muted" hidden><i data-lucide="volume-x"></i></span>
                        </button>
                        <input type="range" class="ba2-audio-slider ba2-audio-volume" id="ba2-audio-volume"
                            min="0" max="100" value="${s}" step="1" style="--value: ${s}%"
                            title="${b(window._t("preview_audio_volume"))}" aria-label="${b(window._t("preview_audio_volume"))}">
                    </div>
                </div>
            </div>
        </div>
    `}function Te(e,t){return`
        <div class="ba2-preview-header">
            <div class="ba2-preview-name">${l(me(e.previewPath))}</div>
            ${t?`<div class="ba2-preview-meta">${l(t)}</div>`:""}
        </div>
    `}function Bt(e){let t=`preview_role_${e||"texture"}`,n=window._t(t);return n===t?window._t("preview_role_texture"):n}function ai(e,t){let n=t.material||{},i=Array.isArray(n.textures)?n.textures:[],o=e.previewMaterialZoom;if(o!=null&&i[o]?.dataUrl){let d=i[o];return`
            ${Te(e,Bt(d.role))}
            <button type="button" class="bundle-text-btn" id="btn-preview-material-back">${l(window._t("preview_material_back"))}</button>
            <div class="ba2-preview-stage"><img alt="" src="${b(d.dataUrl)}"></div>
            <div class="ba2-preview-note">${l(d.path||"")}</div>
        `}let a=[n.signature,n.version?`v${n.version}`:""].filter(Boolean).join(" "),p=i.map((d,r)=>{let u=d.dataUrl?`<button type="button" class="ba2-material-thumb" data-material-texture="${r}"><img alt="" src="${b(d.dataUrl)}"></button>`:`<div class="ba2-material-thumb ba2-material-thumb--missing">${l(window._t("preview_texture_missing"))}</div>`,m=d.found?d.archive||"":window._t("preview_texture_missing");return`
            <div class="ba2-material-row">
                ${u}
                <div class="ba2-material-copy">
                    <div class="ba2-material-role">${l(Bt(d.role))}</div>
                    <div class="ba2-material-path" title="${b(d.path||"")}">${l(d.path||"")}</div>
                    <div class="ba2-material-where">${l(m)}</div>
                </div>
            </div>
        `}).join(""),s=n.template?`<div class="ba2-preview-note">${l(n.template)}</div>`:"";return`
        ${Te(e,a)}
        ${s}
        <div class="ba2-material-list">${p||`<div class="ba2-preview-empty">${l(window._t("preview_unavailable"))}</div>`}</div>
    `}function si(e,t){let n=Math.min(t.size||0,4096),i=t.error==="mesh_too_large"?window._t("preview_mesh_too_large"):t.error==="audio_decode_failed"?window._t("preview_audio_decode_failed"):t.error==="audio_codec_unsupported"?window._t("preview_audio_codec_unsupported"):t.error==="hkx_unsupported"?window._t("preview_hkx_unsupported"):window._t("preview_hex_note",j(n),j(t.size));return`
        ${Te(e,j(t.size))}
        <div class="ba2-preview-note">${l(i)}</div>
        <pre class="ba2-preview-text ba2-preview-hex">${l(ri(t.hex))}</pre>
    `}function ri(e){let t="";try{t=atob(e||"")}catch{return""}let n=[];for(let i=0;i<t.length;i+=16){let o=[],a="";for(let p=0;p<16;p++)if(i+p<t.length){let s=t.charCodeAt(i+p);o.push(s.toString(16).padStart(2,"0")),a+=s>=32&&s<127?t[i+p]:"."}else o.push("  ");n.push(`${i.toString(16).padStart(8,"0")}  ${o.join(" ")}  ${a}`)}return n.join(`
`)}function li(e){let t=e.textureStatus||"ok";if(t==="ok")return"";let n=e.texturePath||e.material||"";return t==="material_not_found"?window._t("preview_tex_material_not_found",n):t==="no_diffuse"?window._t("preview_tex_no_diffuse",n):t==="texture_not_found"?window._t("preview_tex_not_found",n):t==="decode_failed"?window._t("preview_tex_decode_failed",n,e.textureFormat||""):window._t("preview_tex_no_material")}function zt(e,t){let n=t.mesh||{},i=Array.isArray(n.shapes)?n.shapes:[],o=!!e._meshModelVisible&&i.length>0,a=o?i:[],p=n.bones&&Array.isArray(n.bones.names)?n.bones.names.length:0,s=n.animation||null,d=s&&Number(s.frameCount)>1&&Number(s.duration)>0,r=!!s&&p>0,u=p>0&&a.length===0,m=[...new Set(a.map(S=>S.material).filter(Boolean))],$=u?d?window._t("preview_animation",p,Ee(s.duration)):window._t("preview_skeleton",p):window._t("preview_mesh_stats",a.length,n.vertexCount||0,n.triangleCount||0),E=n.skipped?`<div class="ba2-preview-note">${l(window._t("preview_shapes_skipped",n.skipped))}</div>`:"",v=d&&s.trimmed?`<div class="ba2-preview-note">${l(window._t("preview_hkx_trimmed"))}</div>`:"",_=n.modelStatus||"",c=_==="not_found"?`<div class="ba2-preview-note">${l(window._t("preview_hkx_model_not_found"))}</div>`:_==="no_match"?`<div class="ba2-preview-note">${l(window._t("preview_hkx_model_no_match"))}</div>`:o&&n.modelPath?`<div class="ba2-preview-note" title="${b(n.modelPath)}">${l(n.modelPath.split("/").pop())}</div>`:"",h=m.length?`<div class="ba2-preview-note" title="${b(m.join(`
`))}">${l(m[0])}${m.length>1?` +${m.length-1}`:""}</div>`:"",x=a.map(S=>li(S)).filter(Boolean),A=x.length?`<ul class="ba2-preview-reasons">${x.map(S=>`<li>${l(S)}</li>`).join("")}</ul>`:"",f=u?"":`<button type="button" class="bundle-text-btn" id="btn-mesh-wire">${l(window._t("preview_wireframe"))}</button>`,y=!!e._meshModelLoading,T=r?`<button type="button" class="bundle-text-btn${o?" ba2-preview-mesh-tool--on":""}" id="btn-mesh-model" ${y?"disabled":""}>${l(y?window._t("preview_hkx_model_loading"):o?window._t("preview_hkx_hide_model"):window._t("preview_hkx_show_model"))}</button>`:"",L=d?di(s):"";return`
        ${Te(e,$)}
        ${h}
        ${c}
        ${A}
        ${E}
        ${v}
        <div class="ba2-preview-stage ba2-preview-stage--mesh" id="ba2-mesh-stage"></div>
        ${L}
        <div class="ba2-preview-mesh-tools">
            ${T}
            ${f}
            <button type="button" class="bundle-text-btn" id="btn-mesh-reset">${l(window._t("preview_reset_view"))}</button>
            <span class="ba2-preview-mesh-hint">${l(window._t("preview_mesh_controls"))}</span>
        </div>
    `}function di(e){let t=Number(e.duration)||0,n=Ee(t),i=window._t("preview_audio_play");return`
        <div class="ba2-mesh-anim" id="ba2-mesh-anim">
            <button type="button" class="ba2-audio-play" id="ba2-mesh-anim-play"
                title="${b(window._t("preview_shortcut_space",i))}" aria-label="${b(i)}">
                <span id="ba2-mesh-anim-icon-play" hidden><i data-lucide="play"></i></span>
                <span id="ba2-mesh-anim-icon-pause"><i data-lucide="pause"></i></span>
            </button>
            <input type="range" class="ba2-audio-slider ba2-mesh-anim-seek" id="ba2-mesh-anim-seek"
                min="0" max="1000" value="0" step="1" style="--value: 0%"
                aria-label="${b(window._t("preview_audio_play"))}">
            <span class="ba2-audio-time" id="ba2-mesh-anim-time">0:00 / ${l(n)}</span>
            <button type="button" class="ba2-audio-icon-btn ba2-audio-icon-btn--on" id="ba2-mesh-anim-loop"
                title="${b(window._t("preview_audio_loop"))}" aria-label="${b(window._t("preview_audio_loop"))}" aria-pressed="true">
                <i data-lucide="repeat"></i>
            </button>
        </div>
    `}function Ot(e){let t=e.inspectorFilter||"",n=e.ba2Contents||[];return t&&(n=n.filter(i=>tt(String(i.path||""),t))),n}function ci(e,t){if(!t)return e;let n=t.split("/").filter(Boolean),i=e;for(let o of n){if(!i.folders[o])return null;i=i.folders[o]}return i}function ui(e,{foldersWithSubfoldersOnly:t=!1}={}){let n=Ot(e);if(!n.length)return[];let i=et(n),o=[],a=(p,s)=>{for(let d of Object.keys(p.folders).sort((r,u)=>r.localeCompare(u))){let r=p.folders[d],u=s?`${s}/${d}`:d,m=Object.keys(r.folders).length>0;(!t||m)&&o.push(u),a(r,u)}};return a(i,""),o}function pi(e,t){let n=Ot(e);if(!n.length)return"";let i=et(n),o=ci(i,t);if(!o)return"";let a=t.split("/").filter(Boolean).length+1;return it(o,t,e,a,!1)}var ke={renderInspectorContent:St,renderInspectorToolbar:Ft,renderInspectorPathFooter:Lt,renderMeshPreview:zt,formatBa2Size:j,previewNoneMessage:Rt,renderExplorerTreeHtml:ti,getInspectorCountLabel:Je,renderFolderChildrenHtml:pi,collectFolderPaths:ui,renderPreviewPaneHtml:Dt,renderAudioDockHtml:Ut,render(e,t){if(e.isCreating)return this.renderWizard(e,t);let n=(t?.mods??[]).filter(o=>o.isBundle);if(n.length===0)return`
                <div class="bundle-page animate-fade" style="padding: 24px; display: flex; flex-direction: column; gap: 24px; height: 100%;">
                    <div class="mods-toolbar" style="background: var(--bg-surface); border: 1px solid var(--border-color); padding: 16px 24px; border-radius: 12px; display: flex; justify-content: space-between; align-items: center;">
                        <div class="toolbar-left" style="display: flex; align-items: center; gap: 16px;">
                            <div class="header-with-icon" style="display: flex; align-items: center; gap: 12px;">
                                <i data-lucide="archive" style="width: 24px; height: 24px; color: var(--primary-green);"></i>
                                <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t("bundle_creations")}</h2>
                            </div>
                        </div>
                        <div class="tool-buttons">
                            <button class="btn-secondary" id="btn-show-wizard">
                                <i data-lucide="plus"></i> ${window._t("create_new_bundle")}
                            </button>
                        </div>
                    </div>

                    <div class="empty-state-container polished" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; background: rgba(255, 255, 255, 0.02); border: 1px dashed rgba(255, 255, 255, 0.1); border-radius: 16px; text-align: center; padding: 48px;">
                        <div class="empty-state-icon" style="width: 80px; height: 80px; background: rgba(var(--primary-rgb), 0.12); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin-bottom: 24px;">
                            <i data-lucide="layers" style="width: 40px; height: 40px; color: var(--primary-green);"></i>
                        </div>
                        <h3>${window._t("no_bundles_found")}</h3>
                        <p style="color: var(--text-muted); max-width: 400px; line-height: 1.6; margin-bottom: 32px;">${window._t("no_bundles_desc")}</p>
                        <button class="btn primary" id="btn-create-bundle" style="padding: 12px 24px; font-size: 1rem;">
                            <i data-lucide="plus"></i> ${window._t("create_first_bundle")}
                        </button>
                    </div>
                </div>`;let i=o=>o==="enabled"?window._t("in_mod_list"):window._t("storage_only");return`
            <div class="bundle-page animate-fade" style="padding: 24px; display: flex; flex-direction: column; gap: 24px; height: 100%;" onclick="document.querySelectorAll('.bundle-menu-dropdown').forEach(e => e.remove())">
                <style>
                    .staged-item, .picker-item {
                        transition: transform 0.2s, background 0.2s;
                    }
                    .staged-item:hover, .picker-item:hover {
                        background: rgba(255,255,255,0.06) !important;
                        transform: translateX(4px);
                    }
                    .picker-item.selected {
                        border: 1px solid var(--primary-green) !important;
                        background: rgba(var(--primary-rgb), 0.12) !important;
                    }
                    #staged-drop-zone.drag-active {
                        background: rgba(var(--primary-rgb), 0.12) !important;
                        border: 2px dashed var(--primary-green);
                    }
                    .workspace-view .widget {
                        border: 1px solid rgba(255,255,255,0.05);
                        box-shadow: 0 8px 32px rgba(0,0,0,0.3);
                        backdrop-filter: blur(10px);
                    }
                </style>
                <div class="mods-toolbar" style="background: var(--bg-surface); border: 1px solid var(--border-color); padding: 16px 24px; border-radius: 12px; display: flex; justify-content: space-between; align-items: center;">
                    <div class="toolbar-left" style="display: flex; align-items: center; gap: 16px;">
                        <div class="header-with-icon" style="display: flex; align-items: center; gap: 12px;">
                            <i data-lucide="archive" style="width: 24px; height: 24px; color: var(--primary-green);"></i>
                            <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t("bundle_creations")}</h2>
                            <span class="badge" style="background: rgba(255,255,255,0.1);">${n.length}</span>
                        </div>
                    </div>
                    <div class="tool-buttons">
                        <button class="btn-secondary" id="btn-show-wizard">
                            <i data-lucide="plus"></i> ${window._t("create_new_bundle")}
                        </button>
                    </div>
                </div>

                <div class="bundles-grid" style="display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 16px;">
                    ${n.map(o=>`
                        <div class="bundle-card widget" style="padding: 20px; display: flex; flex-direction: column; gap: 16px; position: relative;">
                            <div style="display: flex; justify-content: space-between; align-items: flex-start;">
                                <div style="display: flex; gap: 12px; align-items: center;">
                                    <div style="width: 48px; height: 48px; background: rgba(var(--primary-rgb), 0.12); border-radius: 8px; display: flex; align-items: center; justify-content: center;">
                                        <i data-lucide="package" style="color: var(--primary-green);"></i>
                                    </div>
                                    <div>
                                        <h3 style="margin: 0; font-size: 1.1rem;">${o.name}</h3>
                                        <div style="font-size: 0.8rem; color: var(--text-muted);">${o.originalName}</div>
                                    </div>
                                </div>
                                <div style="display: flex; align-items: center; gap: 8px;">
                                     <button class="btn-icon small bundle-menu-trigger" data-name="${o.originalName.replace(/"/g,"&quot;")}" data-status="${o.status}" onclick="window.nuclearOpenBundleMenu(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();" style="width: 28px; height: 28px; cursor: pointer; pointer-events: auto; z-index: 50; position: relative;">
                                        <i data-lucide="more-vertical" style="width: 16px;"></i>
                                     </button>
                                </div>
                            </div>
                            
                            <div style="display: flex; gap: 8px; margin-top: auto; padding-top: 12px; border-top: 1px solid rgba(255,255,255,0.05); font-size: 0.8rem; color: var(--text-muted);">
                                <span>${i(o.status)}</span>
                                <span style="margin-left: auto;">${window._t("ba2_archive")}</span>
                            </div>
                        </div>
                    `).join("")}
                </div>
            </div>
        `},renderWizard(e,t){let n=Array.from(e.selectedMods),i=Array.from(e.externalFiles),o=Array.from(e.selectedFolders||[]),a=n.length+i.length+o.length;if(e.isPickingInternal)return this.renderInternalPicker(e,t);let p=window._t("bundle_format_auto"),s=e.editingBundle,d=e.canCreateBundle(),r=e.canUseArchiveSelection(),u=e.canExtractSelection(),m=b(window._t("create_ba2_info_label")),$=l(window._t("create_ba2_info"));return`
            <div class="bundle-wizard workspace-view animate-fade">
                <div class="bundle-wizard-toolbar-wrap">
                    <div class="mods-toolbar bundle-wizard-toolbar bundle-wizard-toolbar-main">
                        <div class="toolbar-left bundle-wizard-nav">
                            <button class="btn-icon" id="btn-cancel-wizard" title="${window._t("back")}">
                                <i data-lucide="arrow-left"></i>
                            </button>
                            <div class="header-with-icon">
                                <h2 class="bundle-wizard-title">${s?l(window._t("editing_bundle_title",s.name)):window._t("bundling_workspace")}</h2>
                            </div>
                        </div>
                        <div class="tool-buttons mods-toolbar-actions">
                            <button type="button" class="btn-secondary btn-deploy-primary btn-start-bundling" ${d?"":"disabled"}>
                                <i data-lucide="${s?"save":"zap"}"></i>
                                <span>${window._t(s?"save_bundle":"create_bundle")}</span>
                            </button>
                            <div class="bundle-create-info-wrap">
                                <button type="button" class="btn-secondary bundle-create-info-btn" id="btn-create-ba2-info" aria-expanded="false" aria-controls="create-ba2-info-popover" aria-describedby="create-ba2-info-popover" aria-label="${m}">
                                    <i data-lucide="info"></i>
                                </button>
                                <div class="bundle-create-info-popover" id="create-ba2-info-popover" role="tooltip" hidden>${$}</div>
                            </div>
                            <div class="mods-actions-menu-wrap">
                                <button type="button" class="btn-secondary mods-actions-trigger" id="btn-bundle-actions" aria-haspopup="true" aria-expanded="false" title="${window._t("actions")}">
                                    <i data-lucide="more-horizontal"></i>
                                </button>
                                <div class="mods-actions-dropdown" id="bundle-actions-dropdown" hidden>
                                    <button type="button" class="mods-actions-item" id="btn-add-external">
                                        <i data-lucide="plus"></i>
                                        <span>${window._t("add_external_ba2")}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-add-internal">
                                        <i data-lucide="list-plus"></i>
                                        <span>${window._t("add_mod_from_list")}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-add-folder">
                                        <i data-lucide="folder-plus"></i>
                                        <span>${window._t("add_folder")}</span>
                                    </button>
                                    <div class="mods-actions-divider" role="separator"></div>
                                    <button type="button" class="mods-actions-item" id="btn-menu-select-all" ${r?"":"disabled"}>
                                        <i data-lucide="list-checks"></i>
                                        <span>${window._t("inspector_select_all")}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-menu-deselect-all" ${r?"":"disabled"}>
                                        <i data-lucide="list-minus"></i>
                                        <span>${window._t("inspector_deselect_all")}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-menu-isolate" ${u?"":"disabled"}>
                                        <i data-lucide="package-plus"></i>
                                        <span>${window._t("bundle_add_to_bundle")}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-menu-extract" ${u?"":"disabled"}>
                                        <i data-lucide="folder-open"></i>
                                        <span>${window._t("bundle_extract_to_folder")}</span>
                                    </button>
                                    <div class="mods-actions-divider" role="separator"></div>
                                    <button type="button" class="mods-actions-item danger" id="btn-clear-workspace">
                                        <i data-lucide="trash-2"></i>
                                        <span>${window._t("clear_workspace")}</span>
                                    </button>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="bundle-create-panel">
                        <div class="bundle-create-grid">
                            <label class="bundle-field-group">
                                <span class="bundle-field-label">${window._t("bundle_format")}</span>
                                <select id="bundle-format-select" class="bundle-field">
                                    <option value="Auto" selected>${window._t("bundle_format_auto")}</option>
                                    <option value="General">${window._t("bundle_format_general")}</option>
                                    <option value="DDS">${window._t("bundle_format_dds")}</option>
                                </select>
                            </label>
                            <div class="bundle-field-group bundle-field-group--grow">
                                <span class="bundle-field-label">${window._t("output_name")}</span>
                                <div class="bundle-input-clear-wrap">
                                    <input type="text" id="bundle-name-input" placeholder="MyAmazingBundle" class="bundle-field" value="${b(e.bundleName)}" title="${b(window._t(s?"bundle_edit_name_locked":"created_in_bundles_dir"))}" ${s?"disabled":""}>
                                    <button type="button" class="search-clear" id="bundle-name-clear" title="${b(window._t("clear_text"))}" aria-label="${b(window._t("clear_text"))}" ${e.bundleName&&!s?"":"hidden"}>
                                        <i data-lucide="x"></i>
                                    </button>
                                </div>
                            </div>
                            <label class="bundle-field-group">
                                <span class="bundle-field-label">${window._t("compression_level")}</span>
                                <select id="bundle-compression-select" class="bundle-field">
                                    <option value="Default" selected>${window._t("compression_default")}</option>
                                    <option value="1">${window._t("compression_level_1")}</option>
                                    <option value="2">${window._t("compression_level_2")}</option>
                                    <option value="3">${window._t("compression_level_3")}</option>
                                    <option value="4">${window._t("compression_level_4")}</option>
                                    <option value="5">${window._t("compression_level_5")}</option>
                                    <option value="6">${window._t("compression_level_6")}</option>
                                    <option value="7">${window._t("compression_level_7")}</option>
                                    <option value="8">${window._t("compression_level_8")}</option>
                                    <option value="9">${window._t("compression_level_9")}</option>
                                    <option value="0">${window._t("compression_level_0")}</option>
                                </select>
                            </label>
                        </div>
                        <div class="bundle-create-summary">
                            <span>${window._t("total_items")}: <strong>${a}</strong></span>
                            <span class="bundle-summary-sep">\xB7</span>
                            <span>${window._t("type")}: <strong id="bundle-type-summary">${p}</strong></span>
                        </div>
                    </div>
                </div>

                <div class="workspace-content">
                    <div class="bundle-file-entry-panel widget bundle-wizard-panel">
                        <div class="widget-header bundle-panel-header">
                            <h3>${window._t("file_entry")}</h3>
                            <div id="file-entry-header-toolbar" class="file-entry-header-toolbar">
                                ${e.inspectedBa2Path||e.inspectedFolderPath?Ft(e):""}
                            </div>
                            <span class="bundle-workspace-count">${window._t("total_items")}: ${a}</span>
                        </div>
                        <div class="widget-content bundle-file-entry-body">
                            <div class="staged-list file-entry-drop" id="staged-drop-zone">
                                <div id="ba2-inspector-root">
                                    ${St(e)}
                                </div>
                            </div>
                            <div id="file-entry-path-footer" class="file-entry-path-footer">${Lt(e)}</div>
                        </div>
                    </div>
                </div>
            </div>
        `},renderInternalPicker(e,t){let n=(t?.mods??[]).filter(o=>o.type==="ba2"&&!o.isBundle),i=e.getStagedModCount();return`
            <div class="bundle-wizard workspace-view animate-fade" style="padding: 24px; display: flex; flex-direction: column; gap: 24px; height: 100%;">
                <div class="mods-toolbar" style="background: var(--bg-surface); border: 1px solid var(--border-color); padding: 16px 24px; border-radius: 12px; display: flex; justify-content: space-between; align-items: center;">
                    <div class="toolbar-left" style="display: flex; align-items: center; gap: 16px;">
                        <button class="btn-icon" id="btn-close-picker" title="${window._t("finish_selecting")}">
                            <i data-lucide="arrow-left"></i>
                        </button>
                        <div class="header-with-icon" style="display: flex; align-items: center; gap: 12px;">
                            <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t("select_internal_mods")}</h2>
                            <span class="badge" id="picker-count-badge" style="background: var(--primary-green); color: #121212;">${window._t("n_selected_badge",i)}</span>
                        </div>
                    </div>
                    <button class="btn primary small" id="btn-picker-done">${window._t("done")}</button>
                </div>

                <div class="picker-panel widget" style="flex: 1; display: flex; flex-direction: column; overflow: hidden;">
                    <div class="picker-list" style="flex: 1; overflow-y: auto; padding: 20px; display: grid; grid-template-columns: repeat(auto-fill, minmax(350px, 1fr)); gap: 12px; align-content: start;">
                        ${n.length===0?`
                            <div style="grid-column: 1/-1; padding: 80px; text-align: center; opacity: 0.5;">
                                <i data-lucide="package-search" style="width: 48px; height: 48px; margin-bottom: 16px;"></i>
                                <h3>${window._t("no_ba2_mods_found")}</h3>
                                <p>${window._t("ensure_internal_mods")}</p>
                            </div>
                        `:n.map(o=>{let a=e.isModStaged(o.originalName);return`
                                <div class="picker-item ${a?"selected":""}" data-name="${o.originalName}" style="padding: 16px; background: rgba(255,255,255,0.03); border-radius: 10px; border: 1px solid rgba(255,255,255,0.05); cursor: pointer; display: flex; align-items: center; gap: 16px;">
                                    <div class="picker-checkbox" style="width: 20px; height: 20px; border-radius: 4px; border: 2px solid ${a?"var(--primary-green)":"rgba(255,255,255,0.2)"}; background: ${a?"var(--primary-green)":"transparent"}; display: flex; align-items: center; justify-content: center;">
                                        ${a?'<i data-lucide="check" style="width: 14px; color: #121212;"></i>':""}
                                    </div>
                                    <div style="flex: 1; min-width: 0;">
                                        <div style="font-weight: 600; font-size: 1rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">${o.name}</div>
                                        <div style="font-size: 0.75rem; color: var(--text-muted); opacity: 0.7;">${o.originalName}</div>
                                    </div>
                                </div>
                            `}).join("")}
                    </div>
                </div>
            </div>
        `}};function Ie(e){let t=atob(e||""),n=new Uint8Array(t.length);for(let i=0;i<t.length;i++)n[i]=t.charCodeAt(i);return n}function re(e){let t=Ie(e);return new Float32Array(t.buffer,t.byteOffset,Math.floor(t.byteLength/4))}function fi(e){let t=Ie(e);return new Uint32Array(t.buffer,t.byteOffset,Math.floor(t.byteLength/4))}function vi(e){let t=Ie(e);return new Int32Array(t.buffer,t.byteOffset,Math.floor(t.byteLength/4))}function bi(e){let t=Ie(e);return new Uint16Array(t.buffer,t.byteOffset,Math.floor(t.byteLength/2))}function wi(e,t,n,i,o,a){let p=e[t],s=e[t+1],d=e[t+2],r=e[t+3],u=e[t+4],m=e[t+5],$=e[t+6],E=e[t+7],v=e[t+8],_=e[t+9],c=e[t+10],h=e[t+11],x=n[i],A=n[i+1],f=n[i+2],y=n[i+3],T=n[i+4],L=n[i+5],S=n[i+6],R=n[i+7],C=n[i+8],D=n[i+9],F=n[i+10],V=n[i+11];o[a]=p*x+s*T+d*C,o[a+1]=p*A+s*L+d*D,o[a+2]=p*f+s*S+d*F,o[a+3]=p*y+s*R+d*V+r,o[a+4]=u*x+m*T+$*C,o[a+5]=u*A+m*L+$*D,o[a+6]=u*f+m*S+$*F,o[a+7]=u*y+m*R+$*V+E,o[a+8]=v*x+_*T+c*C,o[a+9]=v*A+_*L+c*D,o[a+10]=v*f+_*S+c*F,o[a+11]=v*y+_*R+c*V+h}function mi(e,t){let n=new Float32Array(16);for(let i=0;i<4;i++)for(let o=0;o<4;o++)n[i*4+o]=e[o]*t[i*4]+e[4+o]*t[i*4+1]+e[8+o]*t[i*4+2]+e[12+o]*t[i*4+3];return n}function hi(e,t,n,i){let o=1/Math.tan(e/2),a=new Float32Array(16);return a[0]=o/Math.max(t,.01),a[5]=o,a[10]=(i+n)/(n-i),a[11]=-1,a[14]=2*i*n/(n-i),a}function _i(e,t,n){let i=Pe(Vt(e,t)),o=Pe(Ce(n,i)),a=Ce(i,o);return new Float32Array([o[0],a[0],i[0],0,o[1],a[1],i[1],0,o[2],a[2],i[2],0,-nt(o,e),-nt(a,e),-nt(i,e),1])}function Vt(e,t){return[e[0]-t[0],e[1]-t[1],e[2]-t[2]]}function nt(e,t){return e[0]*t[0]+e[1]*t[1]+e[2]*t[2]}function Ce(e,t){return[e[1]*t[2]-e[2]*t[1],e[2]*t[0]-e[0]*t[2],e[0]*t[1]-e[1]*t[0]]}function Pe(e){let t=Math.hypot(e[0],e[1],e[2])||1;return[e[0]/t,e[1]/t,e[2]/t]}var yi=`#version 300 es
precision highp float;
precision highp int;
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec2 aUv;
layout(location=3) in vec4 aJoints;
layout(location=4) in vec4 aWeights;
uniform mat4 uMvp;
uniform float uPointSize;
uniform int uSkinned;
uniform highp sampler2D uBones;
uniform int uBoneCount;
out vec3 vNrm;
out vec2 vUv;

void fetchBone(int index, out vec4 r0, out vec4 r1, out vec4 r2) {
    int i = clamp(index, 0, max(uBoneCount - 1, 0));
    r0 = texelFetch(uBones, ivec2(i * 3, 0), 0);
    r1 = texelFetch(uBones, ivec2(i * 3 + 1, 0), 0);
    r2 = texelFetch(uBones, ivec2(i * 3 + 2, 0), 0);
}

void main() {
    vec3 pos = aPos;
    vec3 nrm = aNrm;
    if (uSkinned == 1 && uBoneCount > 0) {
        float wsum = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
        if (wsum > 1e-5) {
            vec4 r0, r1, r2, s0 = vec4(0.0), s1 = vec4(0.0), s2 = vec4(0.0);
            fetchBone(int(aJoints.x + 0.5), r0, r1, r2); s0 += r0 * aWeights.x; s1 += r1 * aWeights.x; s2 += r2 * aWeights.x;
            fetchBone(int(aJoints.y + 0.5), r0, r1, r2); s0 += r0 * aWeights.y; s1 += r1 * aWeights.y; s2 += r2 * aWeights.y;
            fetchBone(int(aJoints.z + 0.5), r0, r1, r2); s0 += r0 * aWeights.z; s1 += r1 * aWeights.z; s2 += r2 * aWeights.z;
            fetchBone(int(aJoints.w + 0.5), r0, r1, r2); s0 += r0 * aWeights.w; s1 += r1 * aWeights.w; s2 += r2 * aWeights.w;
            vec4 hp = vec4(aPos, 1.0);
            pos = vec3(dot(s0, hp), dot(s1, hp), dot(s2, hp));
            nrm = vec3(dot(s0.xyz, aNrm), dot(s1.xyz, aNrm), dot(s2.xyz, aNrm));
        }
    }
    vNrm = nrm;
    vUv = aUv;
    gl_Position = uMvp * vec4(pos, 1.0);
    gl_PointSize = uPointSize;
}`,gi=`#version 300 es
precision mediump float;
in vec3 vNrm;
in vec2 vUv;
uniform sampler2D uTex;
uniform int uHasTex;
uniform int uWire;
out vec4 oColor;
void main() {
    vec3 n = normalize(vNrm);
    vec3 light = normalize(vec3(0.35, 0.55, 0.76));
    float lambert = max(dot(n, light), 0.0);
    vec3 base = uWire == 1 ? vec3(0.75, 0.82, 0.72) : (uHasTex == 1 ? texture(uTex, vUv).rgb : vec3(0.62, 0.64, 0.60));
    oColor = vec4(base * (0.28 + 0.72 * lambert), 1.0);
}`;function Kt(e,t){let n=document.createElement("canvas");n.tabIndex=0,e.appendChild(n);let i=n.getContext("webgl2",{antialias:!0,alpha:!1});if(!i)return e.textContent=typeof window._t=="function"?window._t("preview_webgl_unavailable"):"WebGL2 is not available.",{dispose(){},setWireframe(){},reset(){}};let o=ki(i,yi,gi),a=i.getUniformLocation(o,"uMvp"),p=i.getUniformLocation(o,"uHasTex"),s=i.getUniformLocation(o,"uWire"),d=i.getUniformLocation(o,"uPointSize"),r=i.getUniformLocation(o,"uSkinned"),u=i.getUniformLocation(o,"uBones"),m=i.getUniformLocation(o,"uBoneCount"),$=i.getUniformLocation(o,"uTex"),E=(t.textures||[]).map(w=>({url:w,tex:null,ready:!1})),v=(t.shapes||[]).map(w=>xi(i,w)),_=v.some(w=>w.skinned),c=t.animation&&t.animation.positions?{duration:Math.max(0,Number(t.animation.duration)||0),frameCount:Math.max(0,Number(t.animation.frameCount)||0),trimmed:!!t.animation.trimmed,frames:re(t.animation.positions),matrices:t.animation.matrices?re(t.animation.matrices):null,inverseBind:t.animation.inverseBind?re(t.animation.inverseBind):null}:null,h=t.bones&&t.bones.positions?$i(i,t.bones,{dynamic:!!c}):null;E.forEach(w=>Ti(i,w,()=>ee()));let x=h?h.count:c?.inverseBind?Math.floor(c.inverseBind.length/12):0,A=!!(_&&c?.matrices&&c?.inverseBind&&x>0),f=null,y=A?new Float32Array(x*12):null,T=A?new Float32Array(x*3*4):null;A&&(f=i.createTexture(),i.bindTexture(i.TEXTURE_2D,f),i.texParameteri(i.TEXTURE_2D,i.TEXTURE_MIN_FILTER,i.NEAREST),i.texParameteri(i.TEXTURE_2D,i.TEXTURE_MAG_FILTER,i.NEAREST),i.texParameteri(i.TEXTURE_2D,i.TEXTURE_WRAP_S,i.CLAMP_TO_EDGE),i.texParameteri(i.TEXTURE_2D,i.TEXTURE_WRAP_T,i.CLAMP_TO_EDGE),i.texImage2D(i.TEXTURE_2D,0,i.RGBA32F,x*3,1,0,i.RGBA,i.FLOAT,null));let L=t.bounds||{min:[0,0,0],max:[1,1,1]},S=[(L.min[0]+L.max[0])/2,(L.min[1]+L.max[1])/2,(L.min[2]+L.max[2])/2],R=!!h&&v.length===0,C=Math.max(L.max[0]-L.min[0],L.max[1]-L.min[1],L.max[2]-L.min[2],R?1:.001)*.5,D={yaw:.7,pitch:.45,distance:C/Math.tan(Math.PI/8)*1.15,center:S.slice()},F={yaw:D.yaw,pitch:D.pitch,distance:D.distance,center:D.center.slice()},V=!1,B=!1,O=!!(c&&c.frameCount>1&&c.duration>0),Q=!0,X=0,ne=0,H=0,oe=h?new Float32Array(x*3):null,pe=A?new Float32Array(x*12):null;function fe(w){if(!c||c.frameCount<=0||x<=0)return;let k=Math.max(c.duration,1e-6),M=c.frameCount,U=w;Q?U=(U%k+k)%k:U=Math.min(Math.max(U,0),k);let W=Math.max(M-1,1),Y=M<=1?0:U/k*W,ae=Math.min(M-1,Math.max(0,Math.floor(Y))),ve=Math.min(M-1,ae+1),be=Y-ae;if(h&&oe&&c.frames){let te=x*3,J=ae*te,de=ve*te;for(let I=0;I<oe.length;I++){let N=c.frames[J+I]||0,q=c.frames[de+I]||N;oe[I]=N+(q-N)*be}i.bindBuffer(i.ARRAY_BUFFER,h.positions),i.bufferSubData(i.ARRAY_BUFFER,0,oe)}if(A&&c.matrices&&c.inverseBind&&pe&&y&&T&&f){let te=x*12,J=ae*te,de=ve*te;for(let I=0;I<pe.length;I++){let N=c.matrices[J+I]||0,q=c.matrices[de+I]||N;pe[I]=N+(q-N)*be}for(let I=0;I<x;I++){wi(pe,I*12,c.inverseBind,I*12,y,I*12);let N=I*12,q=I*12;T[q]=y[N],T[q+1]=y[N+1],T[q+2]=y[N+2],T[q+3]=y[N+3],T[q+4]=y[N+4],T[q+5]=y[N+5],T[q+6]=y[N+6],T[q+7]=y[N+7],T[q+8]=y[N+8],T[q+9]=y[N+9],T[q+10]=y[N+10],T[q+11]=y[N+11]}i.activeTexture(i.TEXTURE1),i.bindTexture(i.TEXTURE_2D,f),i.texSubImage2D(i.TEXTURE_2D,0,0,0,x*3,1,i.RGBA,i.FLOAT,T)}}function g(w){if(B)return;ne||(ne=w);let k=Math.min(.1,(w-ne)/1e3);ne=w,O&&c&&(X+=k,!Q&&X>=c.duration&&(X=c.duration,O=!1),fe(X),ee()),O?H=requestAnimationFrame(g):H=0}function P(){B||!O||H||(ne=0,H=requestAnimationFrame(g))}function K(){let w=e.getBoundingClientRect(),k=Math.min(window.devicePixelRatio||1,2),M=Math.max(1,Math.floor(w.width*k)),U=Math.max(1,Math.floor(w.height*k));(n.width!==M||n.height!==U)&&(n.width=M,n.height=U),ee()}function ee(){if(B)return;i.viewport(0,0,n.width,n.height),i.clearColor(.08,.075,.07,1),i.clear(i.COLOR_BUFFER_BIT|i.DEPTH_BUFFER_BIT),i.enable(i.DEPTH_TEST),i.useProgram(o);let w=Wt(F),k=_i(w,F.center,[0,0,1]),M=hi(Math.PI/4,n.width/Math.max(n.height,1),Math.max(C*.01,.01),F.distance+C*8),U=mi(M,k);i.uniformMatrix4fv(a,!1,U),i.uniform1f(d,1),i.uniform1i(s,V?1:0),i.uniform1i(m,x),$&&i.uniform1i($,0),u&&i.uniform1i(u,1),A&&f&&(i.activeTexture(i.TEXTURE1),i.bindTexture(i.TEXTURE_2D,f));for(let W of v){i.bindVertexArray(W.vao),i.uniform1i(r,A&&W.skinned?1:0);let Y=W.texture>=0?E[W.texture]:null;!V&&Y?.ready?(i.activeTexture(i.TEXTURE0),i.bindTexture(i.TEXTURE_2D,Y.tex),i.uniform1i(p,1)):i.uniform1i(p,0),V?(i.bindBuffer(i.ELEMENT_ARRAY_BUFFER,W.lines),i.drawElements(i.LINES,W.lineCount,i.UNSIGNED_INT,0)):(i.bindBuffer(i.ELEMENT_ARRAY_BUFFER,W.indices),i.drawElements(i.TRIANGLES,W.indexCount,i.UNSIGNED_INT,0))}h&&(i.bindVertexArray(h.vao),i.uniform1i(r,0),i.uniform1i(p,0),i.uniform1i(s,1),i.uniform1f(d,1),i.bindBuffer(i.ELEMENT_ARRAY_BUFFER,h.lines),i.drawElements(i.LINES,h.lineCount,i.UNSIGNED_INT,0),i.uniform1f(d,R?10:6),i.drawArrays(i.POINTS,0,h.count)),i.bindVertexArray(null)}let lt=new ResizeObserver(K);lt.observe(e);let ze=!1,dt=!1,Oe=0,We=0;function ct(){let w=Wt(F),k=Pe(Vt(F.center,w)),M=Pe(Ce(k,[0,0,1])),U=Ce(M,k);return{right:M,up:U}}function ye(w,k){let{right:M,up:U}=ct(),W=F.distance*.002;F.center[0]+=(-w*M[0]+k*U[0])*W,F.center[1]+=(-w*M[1]+k*U[1])*W,F.center[2]+=(-w*M[2]+k*U[2])*W}function qe(w,k,M){let U=Math.max(C*.05,Math.min(C*40,F.distance*w)),W=U/F.distance;if(k!=null&&M!=null){let Y=n.getBoundingClientRect(),ae=(k-Y.left)/Math.max(Y.width,1)*2-1,ve=1-(M-Y.top)/Math.max(Y.height,1)*2,{right:be,up:te}=ct(),J=F.distance*Math.tan(Math.PI/8),de=n.width/Math.max(n.height,1),I=1-W;F.center[0]+=(be[0]*ae*J*de+te[0]*ve*J)*I,F.center[1]+=(be[1]*ae*J*de+te[1]*ve*J)*I,F.center[2]+=(be[2]*ae*J*de+te[2]*ve*J)*I}F.distance=U}let ut=w=>{w.preventDefault(),w.stopPropagation(),ze=!0,dt=w.button===1||w.button===2||w.shiftKey,Oe=w.clientX,We=w.clientY,n.focus(),n.setPointerCapture&&n.setPointerCapture(w.pointerId)},pt=w=>{if(!ze)return;let k=w.clientX-Oe,M=w.clientY-We;Oe=w.clientX,We=w.clientY,dt?ye(k,M):(F.yaw+=k*.01,F.pitch=Math.max(-1.2,Math.min(1.2,F.pitch+M*.01))),ee()},ft=()=>{ze=!1},vt=w=>{w.preventDefault(),w.stopPropagation(),qe(w.deltaY>0?1.1:.9,w.clientX,w.clientY),ee()},bt=w=>{w.preventDefault(),w.stopPropagation()},wt=w=>{if(w.key==="ArrowLeft")ye(-24,0);else if(w.key==="ArrowRight")ye(24,0);else if(w.key==="ArrowUp")ye(0,-24);else if(w.key==="ArrowDown")ye(0,24);else if(w.key==="+"||w.key==="=")qe(.9);else if(w.key==="-"||w.key==="_")qe(1.1);else return;w.preventDefault(),ee()},Ve=()=>{F.yaw=D.yaw,F.pitch=D.pitch,F.distance=D.distance,F.center=D.center.slice(),ee()};return n.addEventListener("pointerdown",ut),window.addEventListener("pointermove",pt),window.addEventListener("pointerup",ft),n.addEventListener("wheel",vt,{passive:!1}),n.addEventListener("contextmenu",bt),n.addEventListener("keydown",wt),n.addEventListener("dblclick",Ve),c&&fe(0),K(),P(),{setWireframe(w){V=!!w,ee()},reset:Ve,hasAnimation:!!c,getDuration(){return c?c.duration:0},getTime(){return X},isPlaying(){return O},isLooping(){return Q},isTrimmed(){return!!c?.trimmed},play(){c&&(!Q&&X>=c.duration&&(X=0),O=!0,P())},pause(){O=!1},toggle(){return c?(O?this.pause():this.play(),O):!1},setLoop(w){Q=!!w},seek(w){if(!c)return;let k=Math.max(c.duration,0);X=Math.min(Math.max(Number(w)||0,0),k),fe(X),ee()},dispose(){if(B)return;B=!0,O=!1,H&&cancelAnimationFrame(H),H=0,lt.disconnect(),n.removeEventListener("pointerdown",ut),window.removeEventListener("pointermove",pt),window.removeEventListener("pointerup",ft),n.removeEventListener("wheel",vt),n.removeEventListener("contextmenu",bt),n.removeEventListener("keydown",wt),n.removeEventListener("dblclick",Ve);for(let k of v)i.deleteBuffer(k.positions),i.deleteBuffer(k.normals),i.deleteBuffer(k.uvs),k.joints&&i.deleteBuffer(k.joints),k.weights&&i.deleteBuffer(k.weights),i.deleteBuffer(k.indices),i.deleteBuffer(k.lines),i.deleteVertexArray(k.vao);h&&(i.deleteBuffer(h.positions),i.deleteBuffer(h.normals),i.deleteBuffer(h.uvs),i.deleteBuffer(h.lines),i.deleteVertexArray(h.vao));for(let k of E)k.tex&&i.deleteTexture(k.tex);f&&i.deleteTexture(f),i.deleteProgram(o);let w=i.getExtension("WEBGL_lose_context");w&&w.loseContext(),n.remove()}}}function Wt(e){let t=Math.cos(e.pitch),n=Math.sin(e.pitch);return[e.center[0]+e.distance*t*Math.sin(e.yaw),e.center[1]+e.distance*t*Math.cos(e.yaw),e.center[2]+e.distance*n]}function xi(e,t){let n=re(t.positions),i=Math.floor(n.length/3),o=t.normals?re(t.normals):null;if(!o||o.length<i*3){o=new Float32Array(i*3);for(let y=0;y<i;y++)o[y*3+2]=1}let a=t.uvs?re(t.uvs):null;(!a||a.length<i*2)&&(a=new Float32Array(i*2));let p=fi(t.indices),s=Ei(p),d=t.joints?bi(t.joints):null,r=t.weights?re(t.weights):null,u=!!(d&&r&&d.length>=i*4&&r.length>=i*4),m=new Float32Array(i*4),$=new Float32Array(i*4);if(u)for(let y=0;y<i*4;y++)m[y]=d[y],$[y]=r[y];let E=e.createVertexArray();e.bindVertexArray(E);let v=G(e,e.ARRAY_BUFFER,n);e.enableVertexAttribArray(0),e.vertexAttribPointer(0,3,e.FLOAT,!1,0,0);let _=G(e,e.ARRAY_BUFFER,o);e.enableVertexAttribArray(1),e.vertexAttribPointer(1,3,e.FLOAT,!1,0,0);let c=G(e,e.ARRAY_BUFFER,a);e.enableVertexAttribArray(2),e.vertexAttribPointer(2,2,e.FLOAT,!1,0,0);let h=G(e,e.ARRAY_BUFFER,m);e.enableVertexAttribArray(3),e.vertexAttribPointer(3,4,e.FLOAT,!1,0,0);let x=G(e,e.ARRAY_BUFFER,$);e.enableVertexAttribArray(4),e.vertexAttribPointer(4,4,e.FLOAT,!1,0,0);let A=G(e,e.ELEMENT_ARRAY_BUFFER,p),f=G(e,e.ELEMENT_ARRAY_BUFFER,s);return e.bindVertexArray(null),{vao:E,positions:v,normals:_,uvs:c,joints:h,weights:x,indices:A,lines:f,indexCount:p.length,lineCount:s.length,texture:Number.isInteger(t.texture)?t.texture:-1,skinned:u}}function $i(e,t,{dynamic:n=!1}={}){let i=re(t.positions),o=vi(t.parents),a=Math.floor(i.length/3),p=[];for(let f=0;f<a&&f<o.length;f++){let y=o[f];y>=0&&y<a&&y!==f&&p.push(f,y)}let s=0;for(let f=0;f<a;f++)s=Math.max(s,Math.abs(i[f*3]),Math.abs(i[f*3+1]),Math.abs(i[f*3+2]));let d=Math.max(.25,s*.15),r=[],u=a;for(let f=0;f<a;f++){let y=f<o.length?o[f]:-1;if(y>=0&&y<a&&y!==f)continue;let T=i[f*3],L=i[f*3+1],S=i[f*3+2];r.push(T+d,L,S),p.push(f,u++),r.push(T,L+d,S),p.push(f,u++),r.push(T,L,S+d),p.push(f,u++)}let m=a+r.length/3,$=new Float32Array(m*3);$.set(i),r.length&&$.set(r,a*3);let E=new Float32Array(m*3);for(let f=0;f<m;f++)E[f*3+2]=1;let v=new Uint32Array(p),_=e.createVertexArray();e.bindVertexArray(_);let c=G(e,e.ARRAY_BUFFER,$,n?e.DYNAMIC_DRAW:e.STATIC_DRAW);e.enableVertexAttribArray(0),e.vertexAttribPointer(0,3,e.FLOAT,!1,0,0);let h=G(e,e.ARRAY_BUFFER,E);e.enableVertexAttribArray(1),e.vertexAttribPointer(1,3,e.FLOAT,!1,0,0);let x=G(e,e.ARRAY_BUFFER,new Float32Array(m*2));e.enableVertexAttribArray(2),e.vertexAttribPointer(2,2,e.FLOAT,!1,0,0);let A=G(e,e.ELEMENT_ARRAY_BUFFER,v);return e.bindVertexArray(null),{vao:_,positions:c,normals:h,uvs:x,lines:A,lineCount:v.length,count:a}}function Ei(e){let t=new Uint32Array(e.length*2),n=0;for(let i=0;i+2<e.length;i+=3){let o=e[i],a=e[i+1],p=e[i+2];t[n++]=o,t[n++]=a,t[n++]=a,t[n++]=p,t[n++]=p,t[n++]=o}return t}function G(e,t,n,i=e.STATIC_DRAW){let o=e.createBuffer();return e.bindBuffer(t,o),e.bufferData(t,n,i),o}function Ti(e,t,n){let i=new Image;i.onload=()=>{let o=e.createTexture();e.bindTexture(e.TEXTURE_2D,o),e.pixelStorei(e.UNPACK_FLIP_Y_WEBGL,0),e.texImage2D(e.TEXTURE_2D,0,e.RGBA,e.RGBA,e.UNSIGNED_BYTE,i),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_MIN_FILTER,e.LINEAR_MIPMAP_LINEAR),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_MAG_FILTER,e.LINEAR),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_WRAP_S,e.REPEAT),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_WRAP_T,e.REPEAT),e.generateMipmap(e.TEXTURE_2D),t.tex=o,t.ready=!0,n()},i.src=t.url}function ki(e,t,n){let i=e.createProgram(),o=qt(e,e.VERTEX_SHADER,t),a=qt(e,e.FRAGMENT_SHADER,n);return e.attachShader(i,o),e.attachShader(i,a),e.linkProgram(i),e.deleteShader(o),e.deleteShader(a),i}function qt(e,t,n){let i=e.createShader(t);return e.shaderSource(i,n),e.compileShader(i),i}_t();window._t=(e,...t)=>se.t(e,...t);window.translator=se;window.app={realData:{managerSettings:{}}};var le=e=>window.chrome?.webview?.postMessage(e),Me=null,z=null,ot=0,Z=null,ie=null,he=!1,ue=null,ce=!1,Ae=!1,De=!1,_e=null;function Ne(e,t){let n=document.getElementById("preview-popout-toast");!n||!t||(n.textContent=t,n.className=`logs-popout-toast ${e||"info"}`,n.hidden=!1,requestAnimationFrame(()=>n.classList.add("visible")),clearTimeout(Ne._timer),Ne._timer=setTimeout(()=>{n.classList.remove("visible"),setTimeout(()=>{n.hidden=!0},200)},3200))}function rt(e=window._t("bundle_preview_unsaved_confirm")){return window.appConfirm({title:window._t("bundle_preview_unsaved"),message:e,okText:window._t("bundle_preview_discard"),cancelText:window._t("cancel"),danger:!0})}async function jt(e){let t=await fetch(`https://f76manager.app/locales/${e}.json`);if(!t.ok)throw new Error(`HTTP ${t.status}`);return t.json()}async function Ai(){try{se.loadedLanguages["en-US"]=await jt("en-US"),se.currentLanguage="en-US";let e=new URLSearchParams(window.location.search||"").get("lang")||"";if((!e||e==="auto")&&(e=se.detectLanguage()||"en-US"),e!=="en-US")try{se.loadedLanguages[e]=await jt(e),se.currentLanguage=e}catch(t){console.warn("[PREVIEW-POPOUT] selected locale load failed, using en-US",t)}}catch(e){console.warn("[PREVIEW-POPOUT] locale load failed",e)}}function Li(e){let t=(e.themeId||"").trim()||"fallout",n=typeof e.css=="string"?e.css:"";typeof e.uiAnimations=="boolean"&&document.body.classList.toggle("no-animations",e.uiAnimations===!1),Me&&Me!==t&&document.getElementById(`user-theme-${Me}`)?.remove(),document.documentElement.dataset.theme=t,Me=t;let i=`user-theme-${t}`,o=document.getElementById(i);n?(o||(o=document.createElement("style"),o.id=i),document.head.appendChild(o),o.textContent=n):o&&o.remove()}function Gt(e,t){let n=window.app.realData.managerSettings;e!=null&&(n.configEditorFontSize=e),t!=null&&(n.configEditorFontFamily=t)}function Fi(){Z?.destroy(),Z=null,ie?.dispose(),ie=null,ue&&(ue.abort(),ue=null)}function Le(e){Fi();let t=document.getElementById("preview-popout-root");if(t.innerHTML=`<div class="preview-popout">${e}</div>`,window.lucide)try{window.lucide.createIcons({nodes:[t]})}catch{}return t}function at(e){return`
        <div class="ba2-preview-header">
            <div class="ba2-preview-name">${l(z?.name||"")}</div>
            ${e?`<div class="ba2-preview-meta">${l(e)}</div>`:""}
        </div>`}function st(e){ie?.dispose?.(),ie=null;let t={previewPath:z?.name,_meshModelVisible:ce,_meshModelLoading:Ae},n=Le(ke.renderMeshPreview(t,e)),i=n.querySelector("#ba2-mesh-stage"),o=e.mesh;if(i&&o){let d=ce&&Array.isArray(o.shapes)&&o.shapes.length>0?{...o,bones:null}:{...o,shapes:[],textures:[],vertexCount:0,triangleCount:0};ie=Kt(i,d)}let a=n.querySelector("#btn-mesh-wire");a&&(a.classList.toggle("ba2-preview-mesh-tool--on",he),he&&ie?.setWireframe(!0),a.onclick=()=>{he=!he,a.classList.toggle("ba2-preview-mesh-tool--on",he),ie?.setWireframe(he)});let p=n.querySelector("#btn-mesh-model");if(p&&(p.onclick=()=>{if(Ae)return;let s=Array.isArray(_e?.mesh?.shapes)&&_e.mesh.shapes.length>0;if(ce&&s){ce=!1,st(_e);return}if(s){ce=!0,st(_e);return}Ae=!0,p.disabled=!0,p.textContent=window._t("preview_hkx_model_loading"),Ue({model:!0,quiet:!0})}),n.querySelector("#btn-mesh-reset")?.addEventListener("click",()=>ie?.reset()),Bi(n,ie),window.lucide)try{window.lucide.createIcons({nodes:[n]})}catch{}}function Ue({model:e=!1,quiet:t=!1}={}){if(!z)return;ot+=1,De=!!e,t||(ce=!1,Ae=!1,_e=null,Le(`${at("")}<div class="ba2-preview-empty">${l(window._t("preview_loading"))}</div>`));let n={type:"BUNDLE_PREVIEW_FILE",source:z.source,mode:z.mode,path:z.path,requestId:ot};De&&(n.model=!0),le(n)}function Si(e){if(e.kind==="text"){let i=Le(Re({idPrefix:"popout-text-font",name:z.name,meta:ke.formatBa2Size(e.size),text:e.text,truncated:e.truncated,showPopout:!1}));Z=kt(i,{idPrefix:"popout-text-font",target:z,post:le,confirm:o=>rt(o),onDirtyChange:o=>le({type:"PREVIEW_POPOUT_DIRTY",dirty:o}),onStatus:Ne});return}if(e.kind==="mesh"){_e=e,De&&(Ae=!1,De=!1,ce=Array.isArray(e.mesh?.shapes)&&e.mesh.shapes.length>0),st(e);return}if(e.kind==="image"&&e.dataUrl){let i=[];e.width&&e.height&&i.push(`${e.width}\xD7${e.height}`),e.format&&i.push(e.format),e.size&&i.push(ke.formatBa2Size(e.size)),Le(`${at(i.join(" \xB7 "))}<div class="ba2-preview-stage"><img alt="" src="${b(e.dataUrl)}"></div>`);return}let t=e.kind==="none"?ke.previewNoneMessage(e.error):window._t("bundle_preview_popout_only_main"),n=e.format?e.format:"";Le(`${at(n)}<div class="ba2-preview-empty">${l(t)}</div>`)}function Bi(e,t){if(!t?.hasAnimation||!e)return;let n=e.querySelector("#ba2-mesh-anim-play"),i=e.querySelector("#ba2-mesh-anim-icon-play"),o=e.querySelector("#ba2-mesh-anim-icon-pause"),a=e.querySelector("#ba2-mesh-anim-seek"),p=e.querySelector("#ba2-mesh-anim-time"),s=e.querySelector("#ba2-mesh-anim-loop"),d=t.getDuration(),r=v=>{let _=Math.max(0,Math.floor(v||0));return`${Math.floor(_/60)}:${String(_%60).padStart(2,"0")}`},u=()=>{let v=t.isPlaying();if(i&&(i.hidden=v),o&&(o.hidden=!v),n){let _=window._t(v?"preview_audio_pause":"preview_audio_play");n.title=window._t("preview_shortcut_space",_),n.setAttribute("aria-label",_)}},m=()=>{let v=t.getTime(),_=d>0?Math.min(100,Math.max(0,v/d*100)):0;a&&(a.value=String(Math.round(_*10)),a.style.setProperty("--value",`${_}%`)),p&&(p.textContent=`${r(v)} / ${r(d)}`)},$=0,E=()=>{if($)return;let v=()=>{m(),$=t.isPlaying()?requestAnimationFrame(v):0};$=requestAnimationFrame(v)};n&&(n.onclick=()=>{t.toggle(),u(),t.isPlaying()&&E()}),s&&(s.onclick=()=>{let v=!t.isLooping();t.setLoop(v),s.classList.toggle("ba2-audio-icon-btn--on",v),s.setAttribute("aria-pressed",v?"true":"false")}),a&&(a.oninput=()=>{t.seek(Number(a.value)/1e3*d),m()}),ue&&ue.abort(),ue=new AbortController,document.addEventListener("keydown",v=>{if(v.code!=="Space"||v.ctrlKey||v.metaKey||v.altKey||v.shiftKey)return;let _=v.target;_&&typeof _.closest=="function"&&_.closest("input, textarea, select, [contenteditable], button")||e.querySelector("#ba2-mesh-anim")&&(v.preventDefault(),t.toggle(),u(),t.isPlaying()&&E())},{signal:ue.signal}),u(),m(),t.isPlaying()&&E()}async function Ri(e){if(Z?.isDirty()){if(!await rt())return;le({type:"PREVIEW_POPOUT_DIRTY",dirty:!1})}Gt(e.fontSize,e.fontFamily),z=e.target,document.title=`F76 Manager \u2014 ${z?.name||""}`,Ue()}function Ci(e){let t=Z?.handleMessage(e);if(e.type!=="BUNDLE_TEXT_EDIT_SAVED"||!z||!(String(e.path||"").toLowerCase()===String(z.entryPath||"").replace(/\\/g,"/").replace(/^\/+/,"").toLowerCase())||e.archive!==z.archive)return;let i=Math.max(e.source.lastIndexOf("\\"),e.source.lastIndexOf("/"));z={...z,source:e.source.slice(0,i),mode:"folder",path:e.source.slice(i+1)},t?(le({type:"PREVIEW_POPOUT_DIRTY",dirty:!1}),Ne("success",window._t("bundle_preview_saved",z.name))):Z?.isDirty()||Ue()}async function Pi(){if(Z?.isDirty()){if(!await rt())return;le({type:"PREVIEW_POPOUT_DIRTY",dirty:!1})}le({type:"PREVIEW_POPOUT_CLOSE_CONFIRMED"})}async function Xt(){await Ai(),window.chrome?.webview?.addEventListener("message",e=>{let t=e.data;if(!(!t||typeof t!="object"))switch(t.type){case"THEME_SYNC":Li(t);break;case"PREVIEW_POPOUT_SHOW":Ri(t);break;case"PREVIEW_POPOUT_CLOSE_REQUEST":Pi();break;case"BUNDLE_FILE_PREVIEW":Number(t.requestId)===ot&&Si(t);break;case"BUNDLE_PREVIEW_SOURCE_CHANGED":if(!z||Z?.isDirty()||String(z.mode||"").toLowerCase()!=="folder")break;{let n=String(t.path||"").replace(/\\/g,"/").replace(/^\/+/,"").toLowerCase(),i=String(z.path||"").replace(/\\/g,"/").replace(/^\/+/,"").toLowerCase();n&&n===i&&Ue()}break;case"BUNDLE_TEXT_EDIT_CONTENT":Z?.handleMessage(t);break;case"BUNDLE_TEXT_EDIT_SAVED":case"BUNDLE_TEXT_EDIT_SAVE_FAILED":Ci(t);break;case"CONFIG_EDITOR_FONT_CHANGED":Gt(t.fontSize,t.fontFamily),Z?.applyFont({size:t.fontSize,family:t.fontFamily});break}}),le({type:"PREVIEW_POPOUT_READY"})}document.readyState==="loading"?document.addEventListener("DOMContentLoaded",Xt):Xt();})();
