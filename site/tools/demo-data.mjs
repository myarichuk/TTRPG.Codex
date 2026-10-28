// Ability scores are legal 27-point buys (the system's default method), so no sheet shows a rules warning.
// The campaign the screenshots are taken in, once per language. Everything here is display text
// typed into the app; keys, ids and rules values (class ids, condition ids) stay in English
// because the app stores and compares them that way (docs/LOCALIZATION.md).

const sheet = (cls, species, background, [str, dex, con, int, wis, cha], level, armorClass, alignment) =>
  ({ class: cls, species, background, str, dex, con, int, wis, cha, level, armorClass, speed: 30, alignment });

const party = [
  { id: 'aria', cls: 'fighter', species: 'dwarf', background: 'soldier', scores: [15, 12, 14, 8, 13, 10], level: 5, ac: 19, hp: [38, 52], alignment: 'Lawful Good', player: 0 },
  { id: 'bram', cls: 'rogue', species: 'halfling', background: 'criminal', scores: [8, 15, 14, 12, 13, 10], level: 5, ac: 15, hp: [27, 33], alignment: 'Chaotic Neutral', player: 1 },
  { id: 'ione', cls: 'cleric', species: 'human', background: 'acolyte', scores: [12, 8, 14, 11, 15, 12], level: 5, ac: 18, hp: [24, 38], alignment: 'Neutral Good', player: 2 },
  { id: 'veyra', cls: 'wizard', species: 'elf', background: 'sage', scores: [8, 14, 13, 15, 12, 10], level: 5, ac: 13, hp: [19, 28], alignment: 'Neutral', player: 3 },
];

export const languages = {
  en: {
    tag: 'en',
    dm: 'Mira',
    players: ['Tomas', 'Yael', 'Noor', 'Eli'],
    campaign: {
      name: 'The Sunken Bell',
      tagline: 'A drowned chapel, a missing bell-ringer, and a tide that keeps coming in at the wrong hour.',
      description: 'The fishing village of Saltmere has heard its chapel bell ringing from beneath the harbour for three nights. The party is hired to find out who, or what, is pulling the rope.',
    },
    pcs: { aria: 'Aria Stonehelm', bram: 'Bram Quickfoot', ione: 'Ione Marsh', veyra: 'Veyra Ashdown' },
    npcs: [
      { name: 'Odessa Vale', note: 'Harbourmaster', visibility: 'Known', hp: [11, 11] },
      { name: 'The Bell-Ringer', note: 'Seen on the quay at dusk', visibility: 'Unknown', hp: [30, 30], publicName: 'A figure in a wet cloak' },
      { name: 'Captain Marrow', note: 'Hidden until the third act', visibility: 'Hidden', hp: [65, 65] },
    ],
    monsters: [
      { name: 'Drowned Sailor', hp: [22, 22], count: 3 },
      { name: 'Brine Wraith', hp: [45, 58], count: 1 },
    ],
    wiki: {
      title: 'Saltmere Harbour',
      body: '# Saltmere Harbour\n\nA crescent of grey stone quays under a cliff. The **[[The Drowned Chapel]]** stands half a mile out, its roof visible only at the lowest tide.\n\nOdessa Vale keeps the harbour ledger and knows which boats did not come home.\n\n- Tide tables are pinned to the harbourmaster\'s door\n- The ferry stops running at dusk\n- Nobody will row out after the bell begins',
      second: 'The Drowned Chapel',
      secondBody: 'Built before the harbour wall, swallowed when the sea rose. The bell inside should be rusted through.',
    },
    region: 'The Saltmere Coast',
    places: [
      { name: 'Saltmere Harbour', kind: 'Village' },
      { name: 'The Drowned Chapel', kind: 'Ruin' },
    ],
    fact: 'The chapel bell was cast from the anchors of the ships lost in the Great Storm.',
    session: { title: 'Session 3: The bell tolls', recap: 'The party rowed out at low tide and found the chapel door already open. Inside, three drowned sailors rose from the pews. Aria held the nave while Bram slipped behind the altar and found a ledger with Captain Marrow\'s seal.' },
    conditions: { aria: ['Poisoned', 3], bram: ['Prone', 1] },
  },

  ru: {
    tag: 'ru',
    dm: 'Мира',
    players: ['Тимур', 'Яна', 'Нора', 'Эльдар'],
    campaign: {
      name: 'Затонувший колокол',
      tagline: 'Затопленная часовня, пропавший звонарь и прилив, который приходит не в тот час.',
      description: 'Три ночи подряд жители рыбацкой деревни Солтмир слышат колокол своей часовни из-под воды гавани. Отряд нанят выяснить, кто или что тянет за верёвку.',
    },
    pcs: { aria: 'Ария Каменный Шлем', bram: 'Брам Быстроног', ione: 'Иона Марш', veyra: 'Вейра Пепельная' },
    npcs: [
      { name: 'Одесса Вейл', note: 'Начальница порта', visibility: 'Known', hp: [11, 11] },
      { name: 'Звонарь', note: 'Виден на причале в сумерках', visibility: 'Unknown', hp: [30, 30], publicName: 'Фигура в мокром плаще' },
      { name: 'Капитан Морроу', note: 'Скрыт до третьего акта', visibility: 'Hidden', hp: [65, 65] },
    ],
    monsters: [
      { name: 'Утонувший моряк', hp: [22, 22], count: 3 },
      { name: 'Призрак прибоя', hp: [45, 58], count: 1 },
    ],
    wiki: {
      title: 'Гавань Солтмира',
      body: '# Гавань Солтмира\n\nПолумесяц серых каменных причалов под утёсом. **[[Затопленная часовня]]** стоит в полумиле от берега, и её крышу видно только в самый сильный отлив.\n\nОдесса Вейл ведёт портовую книгу и знает, какие лодки не вернулись.\n\n- Таблицы приливов приколоты к двери начальницы порта\n- Паром перестаёт ходить с наступлением сумерек\n- Никто не выйдет на вёслах, когда начнёт звонить колокол',
      second: 'Затопленная часовня',
      secondBody: 'Построена раньше гаванной стены и поглощена морем, когда оно поднялось. Колокол внутри давно должен был проржаветь.',
    },
    region: 'Побережье Солтмира',
    places: [
      { name: 'Гавань Солтмира', kind: 'Village' },
      { name: 'Затопленная часовня', kind: 'Ruin' },
    ],
    fact: 'Колокол часовни отлит из якорей кораблей, погибших в Великий шторм.',
    session: { title: 'Сессия 3: Колокол звонит', recap: 'Отряд добрался до часовни на лодке в отлив и нашёл дверь уже открытой. Внутри из скамей поднялись три утонувших моряка. Ария удерживала неф, а Брам проскользнул за алтарь и нашёл книгу с печатью капитана Морроу.' },
    conditions: { aria: ['Poisoned', 3], bram: ['Prone', 1] },
  },

  he: {
    tag: 'he',
    dm: 'מירה',
    players: ['תומר', 'יעל', 'נור', 'אלי'],
    campaign: {
      name: 'הפעמון השקוע',
      tagline: 'קפלה טבועה, צלצלן נעדר, ושפל שמגיע בשעה הלא נכונה.',
      description: 'שלושה לילות ברציפות שומעים תושבי כפר הדייגים סולטמיר את פעמון הקפלה מצלצל מתחת למי הנמל. החבורה נשכרה לברר מי, או מה, מושך בחבל.',
    },
    pcs: { aria: 'אריה אבן-קסדה', bram: 'ברם קל-רגל', ione: 'יונה מארש', veyra: 'ורה אפר' },
    npcs: [
      { name: 'אודסה וייל', note: 'מפקחת הנמל', visibility: 'Known', hp: [11, 11] },
      { name: 'הצלצלן', note: 'נראה על המזח בשקיעה', visibility: 'Unknown', hp: [30, 30], publicName: 'דמות במעיל רטוב' },
      { name: 'הקפטן מארו', note: 'מוסתר עד המערכה השלישית', visibility: 'Hidden', hp: [65, 65] },
    ],
    monsters: [
      { name: 'מלח טבוע', hp: [22, 22], count: 3 },
      { name: 'רוח גלים', hp: [45, 58], count: 1 },
    ],
    wiki: {
      title: 'נמל סולטמיר',
      body: '# נמל סולטמיר\n\nסהר של מזחי אבן אפורים מתחת לצוק. **[[הקפלה הטבועה]]** ניצבת חצי מייל מהחוף, וגגה נראה רק בשפל הנמוך ביותר.\n\nאודסה וייל מנהלת את פנקס הנמל ויודעת אילו סירות לא חזרו הביתה.\n\n- לוחות הגאות תלויים על דלת מפקחת הנמל\n- המעבורת מפסיקה לשוט עם רדת החשכה\n- אף אחד לא יחתור החוצה כשהפעמון מתחיל לצלצל',
      second: 'הקפלה הטבועה',
      secondBody: 'נבנתה לפני חומת הנמל, ונבלעה כשהים עלה. הפעמון שבתוכה היה צריך להישחק מזמן מחלודה.',
    },
    region: 'חוף סולטמיר',
    places: [
      { name: 'נמל סולטמיר', kind: 'Village' },
      { name: 'הקפלה הטבועה', kind: 'Ruin' },
    ],
    fact: 'פעמון הקפלה יוצק מעוגני האוניות שאבדו בסופה הגדולה.',
    session: { title: 'מפגש 3: הפעמון מצלצל', recap: 'החבורה חתרה החוצה בשפל ומצאה את דלת הקפלה פתוחה. בפנים קמו שלושה מלחים טבועים מהספסלים. אריה החזיקה את האולם בעוד ברם התגנב מאחורי המזבח ומצא פנקס עם חותם הקפטן מארו.' },
    conditions: { aria: ['Poisoned', 3], bram: ['Prone', 1] },
  },
};

export { party, sheet };
