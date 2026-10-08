using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>ガチャで手に入るキャラ1体分のデータ</summary>
[System.Serializable]
public class GachaCharacter
{
    public string name = "キャラ";
    public Sprite icon;
    [Tooltip("Icon が空のとき Resources/Characters/ から読み込む画像のファイル名（拡張子なし）")]
    public string iconResource;
    public OrbElement element = OrbElement.Fire;
    [Range(1, 5)] public int rarity = 1;
    public int attack = 1000;
    public int hp = 3000;
    public int recovery = 300;
}

/// <summary>強化素材1種類分のデータ</summary>
[System.Serializable]
public class MaterialData
{
    public string name = "素材";
    public Sprite icon;
    [Tooltip("Icon が空のとき Resources/Materials/ から読み込む画像のファイル名（拡張子なし）")]
    public string iconResource;
    [Tooltip("対応する属性（0火 1水 2木 3光 4闇、-1 = 全属性）。同じ属性のキャラに使うと経験値ボーナス")]
    public int element = -1;
    [Tooltip("合成で得られる経験値")]
    public int exp = 150;
    [Range(1, 3)] public int grade = 1;
}

/// <summary>
/// 画面の流れ：スタート → メニュー（バトル / ガチャ / オプション）→ バトル
/// ・空のGameObjectに付けるだけ。画面（Canvas）は再生時に自動で作られます
/// ・パーティーは「持っているキャラの中で属性ごとに一番強いキャラ」を自動で編成
/// ・魔法石・所持キャラは PlayerPrefs に保存
/// </summary>
public class GameFlow : MonoBehaviour
{
    // ============================================================
    // Inspector 設定
    // ============================================================
    [Header("参照（空欄なら自動で探す）")]
    public PuzzleBoard board;

    [Header("見た目")]
    public string gameTitle = "パズル＆バトル";
    [Tooltip("日本語フォント（空欄なら標準フォント。文字化けする場合は Noto Sans JP などを入れる）")]
    public Font font;
    public Sprite titleBackground;
    public Sprite menuBackground;
    public Color titleColor = new Color(0.98f, 0.80f, 0.45f);
    public Color menuColor = new Color(0.62f, 0.82f, 0.98f);
    public Color gachaColor = new Color(0.95f, 0.72f, 0.88f);
    public Color optionColor = new Color(0.78f, 0.80f, 0.86f);
    public Color enhanceColor = new Color(0.62f, 0.86f, 0.70f);

    [Header("ガチャ")]
    [Tooltip("ガチャで出るキャラの一覧。Icon に画像を入れる")]
    public GachaCharacter[] characterPool = DefaultPool();
    [Tooltip("追加キャラ30体。画像は Assets/Resources/Characters/ に入れると自動で読み込まれる")]
    public GachaCharacter[] newCharacters = NewPool();
    [Tooltip("★1〜★4 の出現率（%）")]
    public float[] rarityRates = { 40f, 35f, 20f, 5f };
    public int singleCost = 50;
    public int tenCost = 500;
    [Tooltip("最初に持っている魔法石")]
    public int startStones = 1000;
    [Tooltip("最初から持っているキャラ（Character Pool の番号）")]
    public int[] starterCharacters = { 0, 4, 8, 12, 16 };
    [Tooltip("同じキャラが重なるたびに能力が何％上がるか")]
    public float duplicateBonus = 0.1f;

    [Header("強化素材・合成")]
    [Tooltip("素材の一覧。画像は Assets/Resources/Materials/ に入れると自動で読み込まれる")]
    public MaterialData[] materials = DefaultMaterials();
    [Tooltip("キャラと同じ属性の素材を使ったときの経験値倍率")]
    public float sameElementBonus = 1.5f;
    [Tooltip("1レベル上がるごとの能力の上昇（0.05 = +5%）")]
    public float statPerLevel = 0.05f;
    [Tooltip("次のレベルまでの経験値 = この値 × 今のレベル")]
    public int expPerLevel = 50;

    [Header("敵のドロップ")]
    [Tooltip("雑魚敵がかけらを落とす確率")]
    [Range(0, 1)] public float zakoDropRate = 0.6f;
    [Tooltip("中ボスが素材を落とす確率（そのうち35%は結晶）")]
    [Range(0, 1)] public float chuDropRate = 0.9f;
    [Tooltip("ボスが結晶に加えて虹のオーブを落とす確率")]
    [Range(0, 1)] public float bossRainbowRate = 0.3f;

    [Header("報酬（魔法石）")]
    public int stageClearReward = 20;
    public int allClearReward = 300;

    // ============================================================
    // 内部
    // ============================================================
    enum Page { Title, Menu, Gacha, Enhance, Option, Play }

    static readonly string[] ElementNames = { "火", "水", "木", "光", "闇" };
    const string SaveStones = "pz_stones";
    const string SaveOwned = "pz_owned";
    const string SaveLevels = "pz_levels";
    const string SaveExp = "pz_exp";
    const string SaveMats = "pz_mats";

    int stones;
    int[] owned;
    int[] levels;
    int[] exps;
    int[] mats;
    int[] runDrops;          // 今回のバトルで拾った素材

    // 強化合成画面
    RectTransform enhancePage, enhanceDetail, enhanceMatGrid, enhanceCharContent;
    Text enhanceName, enhanceLevel, enhanceStats, enhanceExpText, enhanceTotal, enhanceMessage;
    RectTransform enhanceExpFill;
    int selectedChar = -1;
    int[] selectedMats;

    Canvas canvas;
    Sprite roundSprite;
    RectTransform titlePage, menuPage, gachaPage, optionPage, playHud, pauseOverlay, resultOverlay;
    CanvasGroup fade;
    Text tapText, menuStonesText, gachaStonesText, gachaMessage, ownedText, optionMessage, resultTitle, resultBody;
    RectTransform menuPartyRow, gachaResultArea;
    Button pullOneButton, pullTenButton, gachaBackButton;
    Page current;
    bool transitioning;

    // スマホ対応（セーフエリア・画面サイズ）
    CanvasScaler scaler;
    readonly List<RectTransform> safeRects = new List<RectTransform>();
    Rect lastSafeArea;
    Vector2Int lastScreenSize;

    // ============================================================
    // 初期データ：メイド★1・制服★2・花飾り★3・姫★4 × 火水木光闇
    // ============================================================
    static GachaCharacter[] DefaultPool()
    {
        string[] kinds = { "メイド", "制服の少女", "花飾りの少女", "姫" };
        int[] atk = { 800, 1100, 1500, 2000 };
        int[] hp = { 2000, 2600, 3300, 4200 };
        int[] rcv = { 200, 280, 350, 450 };

        var list = new List<GachaCharacter>();
        for (int e = 0; e < 5; e++)
            for (int r = 0; r < 4; r++)
                list.Add(new GachaCharacter
                {
                    name = ElementNames[e] + "の" + kinds[r],
                    element = (OrbElement)e,
                    rarity = r + 1,
                    attack = atk[r],
                    hp = hp[r],
                    recovery = rcv[r],
                });
        return list.ToArray();
    }

    /// <summary>素材11種：かけら×5（150EXP）、結晶×5（600EXP）、虹のオーブ（3000EXP・全属性）</summary>
    static MaterialData[] DefaultMaterials()
    {
        string[] keys = { "fire", "water", "wood", "light", "dark" };
        var list = new List<MaterialData>();
        for (int e = 0; e < 5; e++)
            list.Add(new MaterialData { name = ElementNames[e] + "のかけら", iconResource = $"mat_{keys[e]}_shard", element = e, exp = 150, grade = 1 });
        for (int e = 0; e < 5; e++)
            list.Add(new MaterialData { name = ElementNames[e] + "の結晶", iconResource = $"mat_{keys[e]}_crystal", element = e, exp = 600, grade = 2 });
        list.Add(new MaterialData { name = "虹のオーブ", iconResource = "mat_rainbow_orb", element = -1, exp = 3000, grade = 3 });
        return list.ToArray();
    }

    static readonly int[] RarityAtk = { 800, 1100, 1500, 2000, 2600 };
    static readonly int[] RarityHp = { 2000, 2600, 3300, 4200, 5200 };
    static readonly int[] RarityRcv = { 200, 280, 350, 450, 550 };

    /// <summary>追加キャラ30体（ファイル名・名前・属性・レア度）</summary>
    static GachaCharacter[] NewPool()
    {
        var data = new (string file, string name, OrbElement element, int rarity)[]
        {
            ("chara_01_beni", "ベニ", OrbElement.Fire, 1),
            ("chara_02_akane", "アカネ", OrbElement.Fire, 2),
            ("chara_03_hinoko", "ヒノコ", OrbElement.Fire, 2),
            ("chara_04_kaen", "カエン", OrbElement.Fire, 3),
            ("chara_05_flare", "フレア", OrbElement.Fire, 3),
            ("chara_06_homura", "ホムラ", OrbElement.Fire, 4),
            ("chara_07_shizuku", "シズク", OrbElement.Water, 1),
            ("chara_08_nagi", "ナギ", OrbElement.Water, 2),
            ("chara_09_minamo", "ミナモ", OrbElement.Water, 2),
            ("chara_10_ruri", "ルリ", OrbElement.Water, 3),
            ("chara_11_seira", "セイラ", OrbElement.Water, 3),
            ("chara_12_aoi", "アオイ", OrbElement.Water, 4),
            ("chara_13_wakaba", "ワカバ", OrbElement.Wood, 1),
            ("chara_14_konoha", "コノハ", OrbElement.Wood, 2),
            ("chara_15_yuzu", "ユズ", OrbElement.Wood, 2),
            ("chara_16_hisui", "ヒスイ", OrbElement.Wood, 3),
            ("chara_17_morino", "モリノ", OrbElement.Wood, 3),
            ("chara_18_midori", "ミドリ", OrbElement.Wood, 4),
            ("chara_19_kirara", "キララ", OrbElement.Light, 1),
            ("chara_20_asahi", "アサヒ", OrbElement.Light, 2),
            ("chara_21_sora", "ソラ", OrbElement.Light, 2),
            ("chara_22_lux", "ルクス", OrbElement.Light, 3),
            ("chara_23_serena", "セレナ", OrbElement.Light, 3),
            ("chara_24_hikari", "ヒカリ", OrbElement.Light, 4),
            ("chara_25_yami", "ヤミ", OrbElement.Dark, 1),
            ("chara_26_chloe", "クロエ", OrbElement.Dark, 2),
            ("chara_27_shion", "シオン", OrbElement.Dark, 2),
            ("chara_28_yoru", "ヨル", OrbElement.Dark, 3),
            ("chara_29_luna", "ルナ", OrbElement.Dark, 3),
            ("chara_30_mao", "マオ", OrbElement.Dark, 4)
        };
        var list = new List<GachaCharacter>();
        foreach (var d in data)
        {
            int r = Mathf.Clamp(d.rarity, 1, 5) - 1;
            list.Add(new GachaCharacter
            {
                name = d.name,
                iconResource = d.file,
                element = d.element,
                rarity = d.rarity,
                attack = RarityAtk[r],
                hp = RarityHp[r],
                recovery = RarityRcv[r],
            });
        }
        return list.ToArray();
    }

    // ============================================================
    // ライフサイクル
    // ============================================================
    void Awake()
    {
        // 元のキャラ一覧の後ろに追加キャラをつなげる（セーブデータの番号がずれないよう後ろに追加）
        if (newCharacters != null && newCharacters.Length > 0)
        {
            var all = new List<GachaCharacter>(characterPool);
            all.AddRange(newCharacters);
            characterPool = all.ToArray();
        }
        // 画像が空なら Resources から読み込む
        foreach (var c in characterPool)
            if (c != null && c.icon == null && !string.IsNullOrEmpty(c.iconResource))
                c.icon = Resources.Load<Sprite>("Characters/" + c.iconResource);

        foreach (var m in materials)
            if (m != null && m.icon == null && !string.IsNullOrEmpty(m.iconResource))
                m.icon = Resources.Load<Sprite>("Materials/" + m.iconResource);

        if (board == null) board = FindAny<PuzzleBoard>();
        if (board != null) board.autoStart = false;   // バトルはメニューから開始
        PuzzleBoard.ApplyMobileSettings(board != null ? board.targetFrameRate : 60);
    }

    void Start()
    {
        if (board != null)
        {
            board.onStageClear += _ => AddStones(stageClearReward);
            board.onAllClear += OnAllClear;
            board.onGameOver += OnGameOver;
            board.onEnemyDefeated += OnEnemyDefeated;
        }

        LoadData();
        EnsureEventSystem();
        BuildUI();
        SetPage(Page.Title);
    }

    void Update()
    {
        if (current == Page.Title && tapText != null)
        {
            var c = tapText.color;
            c.a = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 4f);
            tapText.color = c;
        }

        // 画面の向き・サイズが変わったらセーフエリアと拡大率を合わせ直す
        var size = new Vector2Int(Screen.width, Screen.height);
        if (Screen.safeArea != lastSafeArea || size != lastScreenSize) ApplyScreenLayout();

        if (BackPressed()) HandleBack();
    }

    // ============================================================
    // スマホ対応：セーフエリア・戻るボタン・アプリ中断
    // ============================================================
    void ApplyScreenLayout()
    {
        lastSafeArea = Screen.safeArea;
        lastScreenSize = new Vector2Int(Screen.width, Screen.height);

        // 縦長のスマホは横幅基準、タブレットや横長の画面は高さ基準で拡大縮小
        if (scaler != null)
        {
            float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
            scaler.matchWidthOrHeight = aspect < 1080f / 1920f ? 0f : 1f;
        }
        foreach (var rt in safeRects)
            if (rt != null) FitSafeArea(rt);
    }

    static void FitSafeArea(RectTransform rt)
    {
        Rect sa = Screen.safeArea;
        float w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
        rt.anchorMin = new Vector2(sa.xMin / w, sa.yMin / h);
        rt.anchorMax = new Vector2(sa.xMax / w, sa.yMax / h);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    /// <summary>セーフエリア（ノッチ・ホームバーを除いた範囲）に合わせた入れ物</summary>
    RectTransform SafeRect(Transform parent)
    {
        var go = new GameObject("SafeArea", typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.pivot = new Vector2(0.5f, 0.5f);
        FitSafeArea(rt);
        safeRects.Add(rt);
        return rt;
    }

    /// <summary>画面いっぱいの背景＋セーフエリアの中身。戻り値は中身（ボタンなどはここに置く）</summary>
    RectTransform MakePage(Transform parent, string name, Color color, Sprite sprite)
    {
        var outer = Panel(parent, name, color, sprite);
        return SafeRect(outer);
    }

    static void Show(RectTransform page, bool on) => page.parent.gameObject.SetActive(on);
    static bool IsShown(RectTransform page) => page != null && page.parent.gameObject.activeSelf;

    static bool BackPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // Android の戻るボタンは Escape キーとして届く
        return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    void HandleBack()
    {
        if (transitioning) return;
        switch (current)
        {
            case Page.Play:
                if (IsShown(resultOverlay)) GoTo(Page.Menu);
                else SetPaused(!IsShown(pauseOverlay));
                break;
            case Page.Gacha:
                if (gachaBackButton == null || gachaBackButton.interactable) GoTo(Page.Menu);
                break;
            case Page.Enhance:
            case Page.Option:
                GoTo(Page.Menu);
                break;
            case Page.Menu:
                GoTo(Page.Title);
                break;
        }
    }

    // アプリが裏に回ったら（電話・ホームボタンなど）バトルを一時停止
    void OnApplicationPause(bool paused)
    {
        if (paused && current == Page.Play && playHud != null && !IsShown(pauseOverlay) && !IsShown(resultOverlay))
            SetPaused(true);
    }

    static T FindAny<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindFirstObjectByType<T>();
#else
        return Object.FindObjectOfType<T>();
#endif
    }

    void EnsureEventSystem()
    {
        var es = FindAny<EventSystem>();
        if (es == null)
        {
            var go = new GameObject("EventSystem");
            es = go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
        // 高解像度のスマホではタップが「ドラッグ」扱いになりやすいので、反応する移動量を画面密度に合わせる
        float dpi = Screen.dpi > 0 ? Screen.dpi : 160f;
        es.pixelDragThreshold = Mathf.Max(10, Mathf.RoundToInt(dpi * 0.08f));
    }

    // ============================================================
    // セーブデータ
    // ============================================================
    static int[] LoadArray(string key, int length, int fill)
    {
        var arr = new int[length];
        for (int i = 0; i < length; i++) arr[i] = fill;
        string[] parts = PlayerPrefs.GetString(key, "").Split(',');
        for (int i = 0; i < length && i < parts.Length; i++)
            if (int.TryParse(parts[i], out int v)) arr[i] = v;
        return arr;
    }

    void LoadData()
    {
        int n = characterPool.Length;
        owned = new int[n];
        levels = LoadArray(SaveLevels, n, 1);
        exps = LoadArray(SaveExp, n, 0);
        mats = LoadArray(SaveMats, materials.Length, 0);
        for (int i = 0; i < n; i++) levels[i] = Mathf.Clamp(levels[i], 1, MaxLevel(i));
        selectedMats = new int[materials.Length];
        selectedChar = -1;

        if (!PlayerPrefs.HasKey(SaveStones))
        {
            stones = startStones;
            foreach (int i in starterCharacters)
                if (i >= 0 && i < n) owned[i] = Mathf.Max(owned[i], 1);
            SaveData();
            return;
        }

        stones = PlayerPrefs.GetInt(SaveStones, startStones);
        string[] parts = PlayerPrefs.GetString(SaveOwned, "").Split(',');
        for (int i = 0; i < n && i < parts.Length; i++)
            int.TryParse(parts[i], out owned[i]);
    }

    void SaveData()
    {
        PlayerPrefs.SetInt(SaveStones, stones);
        PlayerPrefs.SetString(SaveOwned, string.Join(",", owned));
        PlayerPrefs.SetString(SaveLevels, string.Join(",", levels));
        PlayerPrefs.SetString(SaveExp, string.Join(",", exps));
        PlayerPrefs.SetString(SaveMats, string.Join(",", mats));
        PlayerPrefs.Save();
    }

    void ResetData()
    {
        PlayerPrefs.DeleteKey(SaveStones);
        PlayerPrefs.DeleteKey(SaveOwned);
        PlayerPrefs.DeleteKey(SaveLevels);
        PlayerPrefs.DeleteKey(SaveExp);
        PlayerPrefs.DeleteKey(SaveMats);
        LoadData();
        RefreshAll();
    }

    void AddStones(int amount)
    {
        stones += amount;
        SaveData();
        RefreshAll();
    }

    // ============================================================
    // レベル・能力
    // ============================================================
    int MaxLevel(int i) => 10 + 10 * Mathf.Clamp(characterPool[i].rarity, 1, 5);   // ★1:20 ★2:30 ★3:40 ★4:50
    int ExpToNext(int level) => Mathf.Max(1, expPerLevel * level);

    float StatMultiplier(int i, int level)
    {
        float dup = 1f + duplicateBonus * (Mathf.Clamp(owned[i], 1, 10) - 1);
        float lv = 1f + statPerLevel * (level - 1);
        return dup * lv;
    }

    void GetStats(int i, int level, out int atk, out int hp, out int rcv)
    {
        var c = characterPool[i];
        float m = StatMultiplier(i, level);
        atk = Mathf.RoundToInt(c.attack * m);
        hp = Mathf.RoundToInt(c.hp * m);
        rcv = Mathf.RoundToInt(c.recovery * m);
    }

    float Power(int i)
    {
        GetStats(i, levels[i], out int a, out int h, out int r);
        return a + h / 3f + r;
    }

    int MaterialExpFor(int matIndex, int charIndex)
    {
        var m = materials[matIndex];
        bool same = m.element < 0 || m.element == (int)characterPool[charIndex].element;
        return Mathf.RoundToInt(m.exp * (same && m.element >= 0 ? sameElementBonus : 1f));
    }

    /// <summary>経験値を足したあとのレベルと余りの経験値</summary>
    void SimulateExp(int i, int addExp, out int newLevel, out int newExp)
    {
        newLevel = levels[i];
        newExp = exps[i] + addExp;
        int max = MaxLevel(i);
        while (newLevel < max && newExp >= ExpToNext(newLevel))
        {
            newExp -= ExpToNext(newLevel);
            newLevel++;
        }
        if (newLevel >= max) newExp = 0;
    }

    // ============================================================
    // パーティー編成（属性ごとに一番強いキャラ）
    // ============================================================
    List<int> BestPartyIndices()
    {
        var result = new List<int>();
        for (int e = 0; e < 5; e++)
        {
            int best = -1;
            float bestScore = -1f;
            for (int i = 0; i < characterPool.Length; i++)
            {
                var c = characterPool[i];
                if (c == null || owned[i] <= 0 || (int)c.element != e) continue;
                float score = Power(i);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0) result.Add(best);
        }
        return result;
    }

    PartyMember[] BuildParty()
    {
        var list = new List<PartyMember>();
        foreach (int i in BestPartyIndices())
        {
            var c = characterPool[i];
            GetStats(i, levels[i], out int atk, out int hp, out int rcv);
            list.Add(new PartyMember
            {
                name = c.name,
                icon = c.icon,
                element = c.element,
                attack = atk,
                hp = hp,
                recovery = rcv,
            });
        }
        return list.ToArray();
    }

    // ============================================================
    // 画面切り替え
    // ============================================================
    void SetPage(Page p)
    {
        current = p;
        Show(titlePage, p == Page.Title);
        Show(menuPage, p == Page.Menu);
        Show(gachaPage, p == Page.Gacha);
        Show(enhancePage, p == Page.Enhance);
        Show(optionPage, p == Page.Option);
        playHud.gameObject.SetActive(p == Page.Play);
        Show(pauseOverlay, false);
        Show(resultOverlay, false);
        RefreshAll();
    }

    void GoTo(Page p)
    {
        if (!transitioning) StartCoroutine(FadeTo(p));
    }

    IEnumerator FadeTo(Page p)
    {
        transitioning = true;
        fade.blocksRaycasts = true;
        for (float t = 0; t < 0.2f; t += Time.unscaledDeltaTime) { fade.alpha = t / 0.2f; yield return null; }
        fade.alpha = 1f;

        Time.timeScale = 1f;
        if (current == Page.Play && p != Page.Play && board != null) board.StopBattle();
        SetPage(p);
        if (p == Page.Play)
        {
            runDrops = new int[materials.Length];
            if (board != null) board.StartBattle(BuildParty());
        }

        for (float t = 0; t < 0.2f; t += Time.unscaledDeltaTime) { fade.alpha = 1f - t / 0.2f; yield return null; }
        fade.alpha = 0f;
        fade.blocksRaycasts = false;
        transitioning = false;
    }

    void RefreshAll()
    {
        if (menuStonesText != null) menuStonesText.text = $"魔法石  {stones:N0}";
        if (gachaStonesText != null) gachaStonesText.text = $"魔法石  {stones:N0}";

        if (ownedText != null)
        {
            int kinds = 0;
            foreach (int c in owned) if (c > 0) kinds++;
            ownedText.text = $"所持キャラ  {kinds} / {characterPool.Length}";
        }

        if (menuPartyRow != null)
        {
            ClearChildren(menuPartyRow);
            var party = BestPartyIndices();
            float size = 180f;
            for (int k = 0; k < party.Count; k++)
            {
                float x = (k - (party.Count - 1) * 0.5f) * (size + 14f);
                var card = Box(menuPartyRow, "Member", new Vector2(0.5f, 0.5f), new Vector2(x, 0), new Vector2(size, size));
                MakeIcon(card, characterPool[party[k]], size);
                Label(card, $"Lv{levels[party[k]]}", 36, Color.white, new Vector2(0.5f, 0f), new Vector2(0, 4), new Vector2(size, 50));
            }
        }

        if (IsShown(enhancePage)) RefreshEnhance();
    }

    // ============================================================
    // 各画面のボタン処理
    // ============================================================
    /// <summary>敵を倒したときのドロップ（拾った素材はすぐ所持数に加算）</summary>
    int OnEnemyDefeated(EnemyData e)
    {
        int el = Mathf.Clamp((int)e.element, 0, 4);
        var got = new List<int>();
        switch (e.rank)
        {
            case EnemyRank.Zako:
                if (Random.value < zakoDropRate) got.Add(el);                       // かけら
                break;
            case EnemyRank.Chu:
                if (Random.value < chuDropRate) got.Add(Random.value < 0.35f ? 5 + el : el);
                break;
            case EnemyRank.Boss:
                got.Add(5 + el);                                                   // 結晶
                if (Random.value < bossRainbowRate) got.Add(10);                   // 虹のオーブ
                break;
        }

        int count = 0;
        foreach (int m in got)
        {
            if (m < 0 || m >= materials.Length) continue;
            mats[m]++;
            if (runDrops != null) runDrops[m]++;
            count++;
        }
        if (count > 0) SaveData();
        return count;
    }

    string DropSummary()
    {
        if (runDrops == null) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < runDrops.Length; i++)
            if (runDrops[i] > 0)
                sb.Append(sb.Length == 0 ? "" : "、").Append($"{materials[i].name}×{runDrops[i]}");
        return sb.Length == 0 ? "入手素材：なし" : "入手素材：" + sb;
    }

    void OnAllClear()
    {
        AddStones(allClearReward);
        ShowResult("ALL CLEAR!", $"全ステージ制覇！  魔法石 +{allClearReward}\n{DropSummary()}");
    }

    void OnGameOver()
    {
        int st = board != null ? board.CurrentStage : 0;
        ShowResult("GAME OVER", $"ステージ {st} で力尽きた…\n{DropSummary()}");
    }

    void ShowResult(string title, string body)
    {
        resultTitle.text = title;
        resultBody.text = body;
        Show(resultOverlay, true);
    }

    void SetPaused(bool paused)
    {
        Show(pauseOverlay, paused);
        Time.timeScale = paused ? 0f : 1f;
        if (board != null) board.inputLocked = paused;
    }

    void Pull(int times, int cost)
    {
        if (stones < cost)
        {
            gachaMessage.text = "魔法石が足りません";
            return;
        }
        gachaMessage.text = "";
        stones -= cost;

        var results = new List<int>();
        var isNew = new List<bool>();
        for (int i = 0; i < times; i++)
        {
            int idx = RollCharacter();
            if (idx < 0) continue;
            isNew.Add(owned[idx] == 0);
            owned[idx]++;
            results.Add(idx);
        }
        SaveData();
        RefreshAll();
        StartCoroutine(ShowGachaResults(results, isNew));
    }

    int RollCharacter()
    {
        float total = 0f;
        foreach (float r in rarityRates) total += Mathf.Max(0f, r);
        float roll = Random.value * total;
        int rarity = rarityRates.Length;
        for (int i = 0; i < rarityRates.Length; i++)
        {
            roll -= Mathf.Max(0f, rarityRates[i]);
            if (roll < 0f) { rarity = i + 1; break; }
        }

        var candidates = new List<int>();
        for (int i = 0; i < characterPool.Length; i++)
            if (characterPool[i] != null && characterPool[i].rarity == rarity) candidates.Add(i);
        if (candidates.Count == 0)
            for (int i = 0; i < characterPool.Length; i++)
                if (characterPool[i] != null) candidates.Add(i);
        return candidates.Count > 0 ? candidates[Random.Range(0, candidates.Count)] : -1;
    }

    IEnumerator ShowGachaResults(List<int> results, List<bool> isNew)
    {
        pullOneButton.interactable = pullTenButton.interactable = gachaBackButton.interactable = false;
        ClearChildren(gachaResultArea);

        bool single = results.Count == 1;
        float size = single ? 320f : 175f;

        for (int k = 0; k < results.Count; k++)
        {
            Vector2 pos;
            if (single) pos = Vector2.zero;
            else
            {
                int col = k % 5, row = k / 5;
                pos = new Vector2((col - 2) * (size + 18f), row == 0 ? 150f : -150f);
            }

            var c = characterPool[results[k]];
            var card = MakeCard(gachaResultArea, c, isNew[k], pos, size);

            // ポンと出てくる演出
            for (float t = 0; t < 0.15f; t += Time.unscaledDeltaTime)
            {
                card.localScale = Vector3.one * Mathf.Lerp(0.2f, 1.1f, t / 0.15f);
                yield return null;
            }
            card.localScale = Vector3.one;
            yield return new WaitForSecondsRealtime(c.rarity >= 4 ? 0.35f : 0.1f);
        }

        pullOneButton.interactable = pullTenButton.interactable = gachaBackButton.interactable = true;
    }

    // ============================================================
    // UI 作成
    // ============================================================
    void BuildUI()
    {
        roundSprite = CreateRoundedSprite();
        if (font == null)
        {
#if UNITY_2022_2_OR_NEWER
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }

        var cgo = new GameObject("GameUI");
        canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();
        var root = cgo.transform;

        BuildTitle(root);
        BuildMenu(root);
        BuildGacha(root);
        BuildEnhance(root);
        BuildOption(root);
        BuildPlayHud(root);

        // フェード用の黒幕（一番手前）
        var f = Panel(root, "Fade", Color.black, null);
        fade = f.gameObject.AddComponent<CanvasGroup>();
        fade.alpha = 0f;
        fade.blocksRaycasts = false;

        ApplyScreenLayout();
    }

    void BuildTitle(Transform root)
    {
        titlePage = MakePage(root, "TitlePage", titleColor, titleBackground);
        Label(titlePage, gameTitle, 120, Color.white, new Vector2(0.5f, 0.65f), Vector2.zero, new Vector2(1000, 300));
        tapText = Label(titlePage, "TAP TO START", 64, Color.white, new Vector2(0.5f, 0.28f), Vector2.zero, new Vector2(1000, 120));

        // 画面全体を押せるボタン
        var full = Panel(titlePage.parent, "TapArea", new Color(0, 0, 0, 0), null);
        var btn = full.gameObject.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(() => GoTo(Page.Menu));
    }

    void BuildMenu(Transform root)
    {
        menuPage = MakePage(root, "MenuPage", menuColor, menuBackground);

        var bar = Box(menuPage, "TopBar", new Vector2(0.5f, 1f), new Vector2(0, -80), new Vector2(1000, 110));
        bar.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.35f);
        bar.GetComponent<Image>().sprite = roundSprite;
        bar.GetComponent<Image>().type = Image.Type.Sliced;
        menuStonesText = Label(bar, "", 52, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(950, 100));

        Label(menuPage, gameTitle, 90, Color.white, new Vector2(0.5f, 0.84f), Vector2.zero, new Vector2(1000, 160));
        Label(menuPage, "パーティー", 48, Color.white, new Vector2(0.5f, 0.71f), Vector2.zero, new Vector2(600, 80));
        menuPartyRow = Box(menuPage, "PartyRow", new Vector2(0.5f, 0.63f), Vector2.zero, new Vector2(1000, 200));

        MakeButton(menuPage, "バトル開始", new Color(1f, 0.55f, 0.2f), new Vector2(0.5f, 0.47f), Vector2.zero, new Vector2(800, 170), 76,
                   () => GoTo(Page.Play));
        MakeButton(menuPage, "ガチャ", new Color(0.95f, 0.4f, 0.65f), new Vector2(0.5f, 0.365f), Vector2.zero, new Vector2(800, 140), 62,
                   () => GoTo(Page.Gacha));
        MakeButton(menuPage, "強化合成", new Color(0.3f, 0.75f, 0.45f), new Vector2(0.5f, 0.275f), Vector2.zero, new Vector2(800, 140), 62,
                   () => GoTo(Page.Enhance));
        MakeButton(menuPage, "オプション", new Color(0.45f, 0.5f, 0.6f), new Vector2(0.5f, 0.185f), Vector2.zero, new Vector2(800, 120), 54,
                   () => GoTo(Page.Option));
        MakeButton(menuPage, "タイトルへ", new Color(0.3f, 0.3f, 0.35f, 0.8f), new Vector2(0.5f, 0.08f), Vector2.zero, new Vector2(420, 100), 44,
                   () => GoTo(Page.Title));
    }

    void BuildGacha(Transform root)
    {
        gachaPage = MakePage(root, "GachaPage", gachaColor, menuBackground);

        gachaBackButton = MakeButton(gachaPage, "戻る", new Color(0.3f, 0.3f, 0.35f), new Vector2(0f, 1f), new Vector2(140, -80), new Vector2(220, 100), 48,
                                     () => GoTo(Page.Menu));
        Label(gachaPage, "ガチャ", 80, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -80), new Vector2(400, 120));
        gachaStonesText = Label(gachaPage, "", 44, Color.white, new Vector2(1f, 1f), new Vector2(-230, -80), new Vector2(420, 100));

        var rates = new System.Text.StringBuilder();
        for (int i = rarityRates.Length - 1; i >= 0; i--)
            rates.Append($"★{i + 1} {rarityRates[i]}%   ");
        Label(gachaPage, rates.ToString(), 38, Color.white, new Vector2(0.5f, 0.86f), Vector2.zero, new Vector2(1000, 80));

        gachaResultArea = Box(gachaPage, "ResultArea", new Vector2(0.5f, 0.56f), Vector2.zero, new Vector2(1020, 720));
        var bg = gachaResultArea.gameObject.AddComponent<Image>();
        bg.sprite = roundSprite;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0, 0, 0, 0.3f);
        bg.raycastTarget = false;

        gachaMessage = Label(gachaPage, "", 46, new Color(1f, 0.95f, 0.5f), new Vector2(0.5f, 0.28f), Vector2.zero, new Vector2(1000, 80));
        pullOneButton = MakeButton(gachaPage, $"1回\n魔法石 {singleCost}", new Color(0.3f, 0.6f, 1f), new Vector2(0.5f, 0.18f), new Vector2(-250, 0),
                                   new Vector2(440, 170), 50, () => Pull(1, singleCost));
        pullTenButton = MakeButton(gachaPage, $"10連\n魔法石 {tenCost}", new Color(1f, 0.6f, 0.15f), new Vector2(0.5f, 0.18f), new Vector2(250, 0),
                                   new Vector2(440, 170), 50, () => Pull(10, tenCost));
        ownedText = Label(gachaPage, "", 40, Color.white, new Vector2(0.5f, 0.07f), Vector2.zero, new Vector2(1000, 80));
    }

    // ============================================================
    // 強化合成画面
    // ============================================================
    void BuildEnhance(Transform root)
    {
        enhancePage = MakePage(root, "EnhancePage", enhanceColor, menuBackground);
        MakeButton(enhancePage, "戻る", new Color(0.3f, 0.3f, 0.35f), new Vector2(0f, 1f), new Vector2(140, -80), new Vector2(220, 100), 48,
                   () => GoTo(Page.Menu));
        Label(enhancePage, "強化合成", 80, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -80), new Vector2(600, 120));

        // 選んだキャラの詳細
        enhanceDetail = Box(enhancePage, "Detail", new Vector2(0.5f, 0.775f), Vector2.zero, new Vector2(1010, 380));
        var dbg = enhanceDetail.gameObject.AddComponent<Image>();
        dbg.sprite = roundSprite; dbg.type = Image.Type.Sliced; dbg.color = new Color(0, 0, 0, 0.3f); dbg.raycastTarget = false;
        enhanceName = Label(enhanceDetail, "", 54, Color.white, new Vector2(0.65f, 0.85f), Vector2.zero, new Vector2(620, 80));
        enhanceLevel = Label(enhanceDetail, "", 46, new Color(1f, 0.95f, 0.5f), new Vector2(0.65f, 0.65f), Vector2.zero, new Vector2(620, 70));
        var expBg = Box(enhanceDetail, "ExpBar", new Vector2(0.65f, 0.48f), Vector2.zero, new Vector2(560, 34));
        var eb = expBg.gameObject.AddComponent<Image>(); eb.color = new Color(0, 0, 0, 0.5f); eb.raycastTarget = false;
        enhanceExpFill = Box(expBg, "Fill", new Vector2(0f, 0.5f), Vector2.zero, new Vector2(0, 34));
        enhanceExpFill.pivot = new Vector2(0f, 0.5f);
        var ef = enhanceExpFill.gameObject.AddComponent<Image>(); ef.color = new Color(0.4f, 0.85f, 1f); ef.raycastTarget = false;
        enhanceExpText = Label(expBg, "", 28, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560, 34));
        enhanceStats = Label(enhanceDetail, "", 40, Color.white, new Vector2(0.65f, 0.2f), Vector2.zero, new Vector2(620, 140));
        enhanceStats.lineSpacing = 1.1f;

        // 素材
        Label(enhancePage, "素材をタップして選ぶ", 40, Color.white, new Vector2(0.5f, 0.565f), Vector2.zero, new Vector2(1000, 60));
        enhanceMatGrid = Box(enhancePage, "MatGrid", new Vector2(0.5f, 0.47f), Vector2.zero, new Vector2(1010, 300));
        enhanceTotal = Label(enhancePage, "", 42, new Color(1f, 0.95f, 0.5f), new Vector2(0.5f, 0.375f), Vector2.zero, new Vector2(1000, 60));
        MakeButton(enhancePage, "リセット", new Color(0.45f, 0.5f, 0.6f), new Vector2(0.5f, 0.32f), new Vector2(-270, 0), new Vector2(380, 110), 48,
                   () => { ClearSelection(); RefreshEnhance(); });
        MakeButton(enhancePage, "合成する", new Color(1f, 0.55f, 0.2f), new Vector2(0.5f, 0.32f), new Vector2(200, 0), new Vector2(520, 110), 56,
                   DoEnhance);
        enhanceMessage = Label(enhancePage, "", 40, new Color(1f, 1f, 0.6f), new Vector2(0.5f, 0.275f), Vector2.zero, new Vector2(1000, 60));

        // キャラ一覧（スクロール）
        var view = new GameObject("CharList", typeof(RectTransform));
        var vrt = view.GetComponent<RectTransform>();
        vrt.SetParent(enhancePage, false);
        vrt.anchorMin = new Vector2(0.03f, 0.015f);
        vrt.anchorMax = new Vector2(0.97f, 0.25f);
        vrt.offsetMin = vrt.offsetMax = Vector2.zero;
        var vimg = view.AddComponent<Image>();
        vimg.sprite = roundSprite; vimg.type = Image.Type.Sliced; vimg.color = new Color(0, 0, 0, 0.25f);
        view.AddComponent<RectMask2D>();
        var scroll = view.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        var content = new GameObject("Content", typeof(RectTransform));
        enhanceCharContent = content.GetComponent<RectTransform>();
        enhanceCharContent.SetParent(vrt, false);
        enhanceCharContent.anchorMin = new Vector2(0f, 1f);
        enhanceCharContent.anchorMax = new Vector2(1f, 1f);
        enhanceCharContent.pivot = new Vector2(0.5f, 1f);
        enhanceCharContent.offsetMin = enhanceCharContent.offsetMax = Vector2.zero;
        var grid = content.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(150, 175);
        grid.spacing = new Vector2(10, 10);
        grid.padding = new RectOffset(12, 12, 12, 12);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 6;
        grid.childAlignment = TextAnchor.UpperCenter;
        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = enhanceCharContent;
        scroll.viewport = vrt;
    }

    void ClearSelection()
    {
        if (selectedMats == null || selectedMats.Length != materials.Length) selectedMats = new int[materials.Length];
        for (int i = 0; i < selectedMats.Length; i++) selectedMats[i] = 0;
    }

    int SelectedExp()
    {
        if (selectedChar < 0) return 0;
        int total = 0;
        for (int m = 0; m < materials.Length; m++) total += selectedMats[m] * MaterialExpFor(m, selectedChar);
        return total;
    }

    void SelectChar(int i)
    {
        selectedChar = i;
        ClearSelection();
        enhanceMessage.text = "";
        RefreshEnhance();
    }

    void AddMaterial(int m)
    {
        if (selectedChar < 0) { enhanceMessage.text = "先にキャラを選んでください"; return; }
        SimulateExp(selectedChar, SelectedExp(), out int lv, out _);
        if (lv >= MaxLevel(selectedChar)) { enhanceMessage.text = "これ以上レベルは上がりません"; return; }
        if (selectedMats[m] >= mats[m]) { enhanceMessage.text = "素材が足りません"; return; }
        selectedMats[m]++;
        enhanceMessage.text = "";
        RefreshEnhance();
    }

    void DoEnhance()
    {
        if (selectedChar < 0) { enhanceMessage.text = "先にキャラを選んでください"; return; }
        int add = SelectedExp();
        if (add <= 0) { enhanceMessage.text = "素材を選んでください"; return; }

        int before = levels[selectedChar];
        SimulateExp(selectedChar, add, out int lv, out int ex);
        levels[selectedChar] = lv;
        exps[selectedChar] = ex;
        for (int m = 0; m < materials.Length; m++) mats[m] -= selectedMats[m];
        ClearSelection();
        SaveData();

        enhanceMessage.text = lv > before ? $"レベルアップ！ Lv{before} → Lv{lv}" : $"経験値 +{add}";
        RefreshAll();
        RefreshEnhance();
    }

    void RefreshEnhance()
    {
        if (enhancePage == null) return;
        if (selectedMats == null || selectedMats.Length != materials.Length) ClearSelection();

        // 未選択なら最初の所持キャラを選ぶ
        if (selectedChar < 0 || owned[selectedChar] <= 0)
        {
            selectedChar = -1;
            for (int i = 0; i < characterPool.Length; i++) if (owned[i] > 0) { selectedChar = i; break; }
        }

        // --- 詳細 ---
        foreach (Transform t in enhanceDetail)
            if (t.name == "BigIcon") Destroy(t.gameObject);
        if (selectedChar >= 0)
        {
            var c = characterPool[selectedChar];
            var holder = Box(enhanceDetail, "BigIcon", new Vector2(0f, 0.5f), new Vector2(180, 0), new Vector2(300, 300));
            MakeIcon(holder, c, 300);

            int lv = levels[selectedChar], max = MaxLevel(selectedChar);
            int add = SelectedExp();
            SimulateExp(selectedChar, add, out int nlv, out int nex);
            GetStats(selectedChar, lv, out int a0, out int h0, out int r0);
            GetStats(selectedChar, nlv, out int a1, out int h1, out int r1);

            enhanceName.text = $"{c.name}  {new string('★', c.rarity)}";
            enhanceLevel.text = add > 0 && nlv > lv ? $"Lv {lv} → {nlv}  (MAX {max})" : $"Lv {lv}  (MAX {max})";

            float ratio = lv >= max ? 1f : (float)exps[selectedChar] / ExpToNext(lv);
            enhanceExpFill.sizeDelta = new Vector2(560f * Mathf.Clamp01(ratio), 34f);
            enhanceExpText.text = lv >= max ? "MAX" : $"EXP {exps[selectedChar]} / {ExpToNext(lv)}";

            string Arrow(int v0, int v1) => v1 > v0 ? $"{v0:N0} → {v1:N0}" : $"{v0:N0}";
            enhanceStats.text = $"攻撃 {Arrow(a0, a1)}\nHP {Arrow(h0, h1)}\n回復 {Arrow(r0, r1)}";
            enhanceTotal.text = add > 0 ? $"獲得EXP +{add:N0}" : "";
        }
        else
        {
            enhanceName.text = "キャラがいません";
            enhanceLevel.text = enhanceStats.text = enhanceExpText.text = enhanceTotal.text = "";
        }

        // --- 素材 ---
        ClearChildren(enhanceMatGrid);
        float cw = 158f, ch = 140f;
        for (int m = 0; m < materials.Length; m++)
        {
            int col = m % 6, row = m / 6;
            var pos = new Vector2((col - 2.5f) * (cw + 8f), row == 0 ? ch * 0.5f + 4f : -ch * 0.5f - 4f);
            int idx = m;
            var btn = MakeButton(enhanceMatGrid, "", selectedMats[m] > 0 ? new Color(1f, 0.8f, 0.3f, 0.9f) : new Color(1f, 1f, 1f, 0.35f),
                                 new Vector2(0.5f, 0.5f), pos, new Vector2(cw, ch), 10, () => AddMaterial(idx));
            var rt = btn.GetComponent<RectTransform>();
            MakeMaterialIcon(rt, materials[m], 92f, new Vector2(0, 14));
            Label(rt, $"×{mats[m]}", 30, Color.white, new Vector2(0.5f, 0f), new Vector2(0, 18), new Vector2(cw, 36));
            if (selectedMats[m] > 0)
                Label(rt, $"+{selectedMats[m]}", 34, new Color(1f, 0.3f, 0.3f), new Vector2(0.85f, 0.88f), Vector2.zero, new Vector2(80, 40));
            btn.interactable = mats[m] > 0;
        }

        // --- キャラ一覧 ---
        ClearChildren(enhanceCharContent);
        var order = new List<int>();
        for (int i = 0; i < characterPool.Length; i++) if (owned[i] > 0) order.Add(i);
        order.Sort((x, y) => characterPool[x].element != characterPool[y].element
            ? characterPool[x].element.CompareTo(characterPool[y].element)
            : characterPool[y].rarity.CompareTo(characterPool[x].rarity));

        foreach (int i in order)
        {
            int idx = i;
            var cell = new GameObject("Char", typeof(RectTransform));
            var crt = cell.GetComponent<RectTransform>();
            crt.SetParent(enhanceCharContent, false);
            var bg = cell.AddComponent<Image>();
            bg.sprite = roundSprite; bg.type = Image.Type.Sliced;
            bg.color = i == selectedChar ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 1f, 1f, 0.15f);
            var b = cell.AddComponent<Button>();
            b.targetGraphic = bg;
            b.onClick.AddListener(() => SelectChar(idx));
            var iconHolder = Box(crt, "Icon", new Vector2(0.5f, 0.6f), Vector2.zero, new Vector2(130, 130));
            MakeIcon(iconHolder, characterPool[i], 130);
            Label(crt, levels[i] >= MaxLevel(i) ? "Lv MAX" : $"Lv{levels[i]}", 30, Color.white, new Vector2(0.5f, 0f), new Vector2(0, 20), new Vector2(150, 36));
        }
    }

    /// <summary>素材アイコン（画像がなければ属性色の丸）</summary>
    void MakeMaterialIcon(RectTransform parent, MaterialData m, float size, Vector2 pos)
    {
        var rt = Box(parent, "MatIcon", new Vector2(0.5f, 0.5f), pos, new Vector2(size, size));
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        if (m.icon != null)
        {
            img.sprite = m.icon;
            img.preserveAspect = true;
        }
        else
        {
            img.sprite = roundSprite;
            img.type = Image.Type.Sliced;
            img.color = m.element >= 0 ? ElementColor((OrbElement)m.element) : new Color(1f, 1f, 1f);
            Label(rt, m.grade >= 3 ? "虹" : m.grade == 2 ? "晶" : "片", Mathf.RoundToInt(size * 0.5f), Color.white,
                  new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        }
    }

    void BuildOption(Transform root)
    {
        optionPage = MakePage(root, "OptionPage", optionColor, menuBackground);
        MakeButton(optionPage, "戻る", new Color(0.3f, 0.3f, 0.35f), new Vector2(0f, 1f), new Vector2(140, -80), new Vector2(220, 100), 48,
                   () => GoTo(Page.Menu));
        Label(optionPage, "オプション", 80, Color.white, new Vector2(0.5f, 1f), new Vector2(0, -80), new Vector2(600, 120));
        Label(optionPage, "（設定項目は今後追加予定）", 46, Color.white, new Vector2(0.5f, 0.6f), Vector2.zero, new Vector2(1000, 100));

        optionMessage = Label(optionPage, "", 44, new Color(1f, 0.95f, 0.5f), new Vector2(0.5f, 0.3f), Vector2.zero, new Vector2(1000, 80));
        MakeButton(optionPage, "データをリセット", new Color(0.85f, 0.3f, 0.3f), new Vector2(0.5f, 0.4f), Vector2.zero, new Vector2(700, 140), 52,
                   () => { ResetData(); optionMessage.text = "データをリセットしました"; });
    }

    void BuildPlayHud(Transform root)
    {
        // 盤面の操作を邪魔しないよう、背景なしの入れ物にする
        var go = new GameObject("PlayHud", typeof(RectTransform));
        playHud = go.GetComponent<RectTransform>();
        playHud.SetParent(root, false);
        Stretch(playHud);

        var hudSafe = SafeRect(playHud);
        MakeButton(hudSafe, "MENU", new Color(0.2f, 0.2f, 0.25f, 0.85f), new Vector2(1f, 1f), new Vector2(-110, -70), new Vector2(180, 90), 40,
                   () => SetPaused(true));

        // 一時停止
        pauseOverlay = MakePage(playHud, "Pause", new Color(0, 0, 0, 0.65f), null);
        Label(pauseOverlay, "一時停止", 90, Color.white, new Vector2(0.5f, 0.65f), Vector2.zero, new Vector2(800, 150));
        MakeButton(pauseOverlay, "再開", new Color(0.3f, 0.7f, 0.4f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600, 150), 60,
                   () => SetPaused(false));
        MakeButton(pauseOverlay, "メニューへ戻る", new Color(0.85f, 0.35f, 0.35f), new Vector2(0.5f, 0.38f), Vector2.zero, new Vector2(600, 150), 56,
                   () => GoTo(Page.Menu));

        // 結果
        resultOverlay = MakePage(playHud, "Result", new Color(0, 0, 0, 0.7f), null);
        resultTitle = Label(resultOverlay, "", 120, new Color(1f, 0.85f, 0.3f), new Vector2(0.5f, 0.62f), Vector2.zero, new Vector2(1000, 200));
        resultBody = Label(resultOverlay, "", 46, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(960, 260));
        resultBody.horizontalOverflow = HorizontalWrapMode.Wrap;
        MakeButton(resultOverlay, "メニューへ", new Color(1f, 0.55f, 0.2f), new Vector2(0.5f, 0.36f), Vector2.zero, new Vector2(600, 150), 60,
                   () => GoTo(Page.Menu));
    }

    // ============================================================
    // UI 部品
    // ============================================================
    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void ClearChildren(Transform t)
    {
        for (int i = t.childCount - 1; i >= 0; i--) Destroy(t.GetChild(i).gameObject);
    }

    RectTransform Panel(Transform parent, string name, Color color, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Stretch(rt);
        var img = go.AddComponent<Image>();
        img.color = sprite != null ? Color.white : color;
        if (sprite != null)
        {
            img.sprite = sprite;
            img.preserveAspect = false;
        }
        return rt;
    }

    RectTransform Box(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    Text Label(Transform parent, string text, int size, Color color, Vector2 anchor, Vector2 pos, Vector2 boxSize)
    {
        var rt = Box(parent, "Text", anchor, pos, boxSize);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = FontStyle.Bold;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        var o = rt.gameObject.AddComponent<Outline>();
        o.effectColor = new Color(0, 0, 0, 0.6f);
        o.effectDistance = new Vector2(3, -3);
        return t;
    }

    Button MakeButton(Transform parent, string label, Color color, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize, UnityAction onClick)
    {
        var rt = Box(parent, "Button_" + label, anchor, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = roundSprite;
        img.type = Image.Type.Sliced;
        img.color = color;
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);
        var text = Label(rt, label, fontSize, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, size);
        text.lineSpacing = 0.9f;
        return btn;
    }

    Color ElementColor(OrbElement e)
    {
        if (board != null && board.orbColors != null && board.orbColors.Length > (int)e) return board.orbColors[(int)e];
        Color[] d = { new Color(0.95f, 0.3f, 0.3f), new Color(0.3f, 0.55f, 1f), new Color(0.35f, 0.85f, 0.4f),
                      new Color(1f, 0.85f, 0.3f), new Color(0.7f, 0.4f, 0.95f) };
        return d[(int)e % d.Length];
    }

    /// <summary>キャラのアイコン（画像がなければ属性色の四角＋属性名）</summary>
    void MakeIcon(RectTransform parent, GachaCharacter c, float size)
    {
        var rt = Box(parent, "Icon", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        if (c.icon != null)
        {
            img.sprite = c.icon;
            img.preserveAspect = true;
        }
        else
        {
            img.sprite = roundSprite;
            img.type = Image.Type.Sliced;
            img.color = ElementColor(c.element);
            Label(rt, ElementNames[(int)c.element % 5], Mathf.RoundToInt(size * 0.45f), Color.white,
                  new Vector2(0.5f, 0.55f), Vector2.zero, new Vector2(size, size * 0.6f));
            Label(rt, new string('★', c.rarity), Mathf.RoundToInt(size * 0.16f), new Color(1f, 0.9f, 0.3f),
                  new Vector2(0.5f, 0.15f), Vector2.zero, new Vector2(size, size * 0.25f));
        }
    }

    /// <summary>ガチャ結果のカード</summary>
    RectTransform MakeCard(RectTransform parent, GachaCharacter c, bool isNew, Vector2 pos, float size)
    {
        Color[] rarityColors =
        {
            new Color(0.6f, 0.6f, 0.65f),   // ★1
            new Color(0.4f, 0.75f, 0.5f),   // ★2
            new Color(0.4f, 0.6f, 1f),      // ★3
            new Color(1f, 0.8f, 0.2f),      // ★4
            new Color(1f, 0.45f, 0.8f),     // ★5
        };

        var card = Box(parent, "Card", new Vector2(0.5f, 0.5f), pos, new Vector2(size + 16f, size * 1.4f));
        var bg = card.gameObject.AddComponent<Image>();
        bg.sprite = roundSprite;
        bg.type = Image.Type.Sliced;
        bg.color = rarityColors[Mathf.Clamp(c.rarity - 1, 0, rarityColors.Length - 1)];
        bg.raycastTarget = false;

        var iconHolder = Box(card, "IconHolder", new Vector2(0.5f, 0.6f), Vector2.zero, new Vector2(size, size));
        MakeIcon(iconHolder, c, size * 0.95f);

        Label(card, c.name, Mathf.RoundToInt(size * 0.12f), Color.white, new Vector2(0.5f, 0.1f), Vector2.zero, new Vector2(size * 1.2f, size * 0.2f));
        if (isNew)
            Label(card, "NEW!", Mathf.RoundToInt(size * 0.16f), new Color(1f, 0.3f, 0.3f), new Vector2(0.2f, 0.95f), Vector2.zero, new Vector2(size, size * 0.2f));
        return card;
    }

    /// <summary>角丸の四角（ボタン・カード用）を生成</summary>
    static Sprite CreateRoundedSprite()
    {
        const int size = 64, radius = 22;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                float a = Mathf.Clamp01(radius - d + 0.5f);
                px[y * size + x] = new Color(1, 1, 1, a);
            }
        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(radius + 2, radius + 2, radius + 2, radius + 2));
    }
}
