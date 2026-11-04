// Generates spike HTML: usage node gen.js <pages-multiplier> > out.html
const mult = +process.argv[2] || 1;
const lorem = 'The tide gnaws the Grey Coast at night, and the salt-cured watchmen of Saltmere count what it leaves behind: planks, rope, and sometimes people. ';
const ru = 'Капитан Вейл не доверяет никому из команды, но прилив не ждёт, и ей приходится платить золотом за молчание. ';
const he = 'הקפטן וייל לא סומכת על איש מהצוות, אבל הגאות לא מחכה, והיא נאלצת לשלם זהב תמורת שתיקה. ';
const entries = n => Array.from({length:n},(_,i)=>`<div class="entry"><h3>Place ${i+1}: The Salt Lantern</h3><p>${lorem.repeat(2)}</p><ul><li>Lock DC 15; Maud has the key</li><li>Trap: pit, 2d10</li></ul></div>`).join('');
const stat = `<div class="monster"><h3>Goblin Warrior</h3><p><em>Small Humanoid, Neutral Evil</em></p><hr><p><b>AC</b> 15 &nbsp; <b>HP</b> 10 (3d6) &nbsp; <b>Speed</b> 30 ft.</p><table><tr><th>STR</th><th>DEX</th><th>CON</th><th>INT</th><th>WIS</th><th>CHA</th></tr><tr><td>8 (−1)</td><td>14 (+2)</td><td>10 (+0)</td><td>10 (+0)</td><td>8 (−1)</td><td>8 (−1)</td></tr></table><hr><p><b>Scimitar.</b> <i>Melee Attack:</i> +4 to hit, reach 5 ft. <i>Hit:</i> 5 (1d6 + 2) slashing damage.</p></div>`;
const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Spike</title><style>
@font-face{font-family:Cinzel;src:url(http://book.local/fonts/cinzel/cinzel-latin-wght-normal.woff2);font-weight:400 900}
@font-face{font-family:AlegreyaSans;src:url(http://book.local/fonts/alegreya-sans/alegreya-sans-latin-400-normal.woff2);font-weight:400;unicode-range:U+0000-00FF,U+2013-2014,U+2018-201D,U+2212}
@font-face{font-family:AlegreyaSans;src:url(http://book.local/fonts/alegreya-sans/alegreya-sans-latin-700-normal.woff2);font-weight:700;unicode-range:U+0000-00FF,U+2013-2014,U+2018-201D,U+2212}
@font-face{font-family:AlegreyaSans;src:url(http://book.local/fonts/alegreya-sans/alegreya-sans-latin-400-italic.woff2);font-weight:400;font-style:italic;unicode-range:U+0000-00FF,U+2013-2014,U+2018-201D,U+2212}
@font-face{font-family:AlegreyaSans;src:url(http://book.local/fonts/alegreya-sans/alegreya-sans-cyrillic-400-normal.woff2);font-weight:400;unicode-range:U+0301,U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116}
@font-face{font-family:AlegreyaSans;src:url(http://book.local/fonts/alegreya-sans/alegreya-sans-cyrillic-700-normal.woff2);font-weight:700;unicode-range:U+0301,U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116}
@font-face{font-family:Forum;src:url(http://book.local/fonts/forum/forum-cyrillic-400-normal.woff2);unicode-range:U+0301,U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116}
@font-face{font-family:Assistant;src:url(http://book.local/fonts/assistant/assistant-hebrew-wght-normal.woff2);font-weight:200 800;unicode-range:U+0590-05FF,U+200C-2010,U+20AA,U+25CC,U+FB1D-FB4F}
@font-face{font-family:FrankRuhl;src:url(http://book.local/fonts/frank-ruhl-libre/frank-ruhl-libre-hebrew-wght-normal.woff2);font-weight:300 900;unicode-range:U+0590-05FF,U+200C-2010,U+20AA,U+25CC,U+FB1D-FB4F}
@page{size:A4;margin:20mm 18mm 22mm;@bottom-center{content:"— " counter(page) " / " counter(pages) " —";font:9pt Cinzel,serif}}
@page :left{margin-left:24mm;margin-right:14mm;@bottom-left{content:counter(page);font:9pt Cinzel,serif}}
@page :right{margin-left:14mm;margin-right:24mm;@bottom-right{content:counter(page);font:9pt Cinzel,serif}}
html{font:11pt/1.35 AlegreyaSans,Assistant,serif;color:#222}
h1,h2,h3{font-family:Cinzel,Forum,FrankRuhl,serif;break-after:avoid}
h1{font-size:26pt;break-before:page}
.cols{column-count:2;column-gap:8mm;column-fill:balance}
.entry{break-inside:avoid;margin-bottom:4mm}
.monster{break-inside:avoid;border:2px solid #7a1c1c;background:#fdf1dc;padding:3mm;margin:4mm 0}
.monster table{width:100%;text-align:center}
.toc a::after{content:" ........ " target-counter(attr(href),page)}
.mask{width:120mm;height:60mm;background:linear-gradient(135deg,#7a1c1c,#e0b050 50%,#234);
  -webkit-mask-image:url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 50' preserveAspectRatio='none'><path d='M0 4 Q10 0 20 5 T40 4 T60 6 T80 3 T100 5 L100 46 Q90 50 80 45 T60 47 T40 44 T20 48 T0 45Z' fill='black'/></svg>");
  -webkit-mask-size:100% 100%;mask-size:100% 100%}
.svgclip{width:120mm;height:60mm}
.rtl{direction:rtl;font-family:Assistant,AlegreyaSans,serif}
.fallback{font-family:Cinzel,serif}
</style></head><body>
<h1 id="toc-h">Contents</h1>
<div class="toc"><p><a href="#gaz">Gazetteer</a></p><p><a href="#stat">Bestiary</a></p><p><a href="#heb">עברית</a></p></div>
<h1 id="gaz">Gazetteer</h1><div class="cols">${entries(14*mult)}</div>
<h1 id="stat">Bestiary</h1><div class="cols">${stat.repeat(6*mult)}</div>
<h2>Masks</h2>
<div class="mask"></div>
<svg class="svgclip" viewBox="0 0 100 50"><defs><linearGradient id="g"><stop offset="0" stop-color="#7a1c1c"/><stop offset=".5" stop-color="#e0b050"/><stop offset="1" stop-color="#234"/></linearGradient><clipPath id="c"><path d="M0 4 Q10 0 20 5 T40 4 T60 6 T80 3 T100 5 L100 46 Q90 50 80 45 T60 47 T40 44 T20 48 T0 45Z"/></clipPath></defs><rect width="100" height="50" fill="url(#g)" clip-path="url(#c)"/></svg>
<h1 id="heb">עברית ורוסית</h1>
<div class="rtl"><h2>הקפטן וייל</h2><p>${he.repeat(8)}</p><p>Mixed: Level 3 <b>Goblin</b> עם 15 נקודות חיים (HP 10).</p></div>
<h2>Русский</h2><p>${ru.repeat(8)}</p>
<h2 class="fallback">Fallback probe</h2><p class="fallback">Latin ok. Hebrew in Cinzel stack: שלום. Cyrillic in Cinzel stack: Привет. Missing glyphs: 漢字 ✦ ☠ ∑ العربية <b><i>bold italic synth</i></b></p>
</body></html>`;
process.stdout.write(html);
