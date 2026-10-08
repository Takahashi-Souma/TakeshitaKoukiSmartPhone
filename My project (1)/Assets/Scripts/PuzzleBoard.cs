using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>ドロップ／キャラの属性（数字はドロップの種類番号と対応）</summary>
public enum OrbElement { Fire = 0, Water = 1, Wood = 2, Light = 3, Dark = 4 }

/// <summary>パーティーの1体分のデータ</summary>
[System.Serializable]
public class PartyMember
{
    public string name = "キャラ";
    [Tooltip("キャラのアイコン画像（空欄なら属性色の仮アイコン）")]
    public Sprite icon;
    public OrbElement element = OrbElement.Fire;
    public int attack = 1000;
    public int hp = 3000;
    public int recovery = 300;
}

/// <summary>敵のランク</summary>
public enum EnemyRank { Zako, Chu, Boss }

/// <summary>敵1種類分のデータ</summary>
[System.Serializable]
public class EnemyData
{
    public string name = "スライム";
    [Tooltip("敵の画像（空欄なら属性色の丸）")]
    public Sprite sprite;
    [Tooltip("Zako = 雑魚 / Chu = 中ボス / Boss = ボス")]
    public EnemyRank rank = EnemyRank.Zako;
    public OrbElement element = OrbElement.Fire;
    public long hp = 8000;
    public int attack = 1000;
    [Tooltip("何ターンごとに攻撃してくるか")]
    [Range(1, 5)] public int turns = 2;
}

/// <summary>
/// パズドラ風ドロップパズル＋簡易バトル
/// ・空のGameObjectに付けるだけで動作（Scene画面にもプレビュー表示）
/// ・ドロップを動かして3つ以上揃えると消える → 落ちコン
/// ・消した色と同じ属性のキャラが攻撃。コンボ数に応じて攻撃力アップ
/// ・回復ドロップでHP回復。敵は数ターンごとに攻撃してくる
/// </summary>
[ExecuteAlways]
public class PuzzleBoard : MonoBehaviour
{
    // ============================================================
    // Inspector 設定
    // ============================================================
    [Header("ゲーム進行")]
    [Tooltip("ON：再生したらすぐバトル開始（GameFlow を使うときは自動でOFFになる）")]
    public bool autoStart = true;

    /// <summary>ステージクリア時（引数 = クリアしたステージ）</summary>
    public System.Action<int> onStageClear;
    /// <summary>最終ステージクリア時（設定されていればループせずここで終了）</summary>
    public System.Action onAllClear;
    /// <summary>全滅時（設定されていればやり直さずここで終了）</summary>
    public System.Action onGameOver;
    /// <summary>敵を倒したとき。戻り値 = ドロップした素材の数（0 なら表示なし）</summary>
    public System.Func<EnemyData, int> onEnemyDefeated;
    /// <summary>true の間は操作を受け付けない（一時停止など）</summary>
    [System.NonSerialized] public bool inputLocked;

    public int CurrentStage => stage;

    [Header("盤面サイズ")]
    public int columns = 6;
    public int rows = 5;
    public float cellSize = 1f;

    [Header("画面レイアウト")]
    [Tooltip("ON：盤面を画面の横幅いっぱいにして下に寄せる（パズドラ風）")]
    public bool boardAtBottom = true;
    [Tooltip("盤面の左右の余白（マス数単位）")]
    public float sideMargin = 0f;
    [Tooltip("盤面の下の余白（マス数単位）")]
    public float bottomMargin = 0.3f;

    [Header("見た目（明るさ）")]
    [Tooltip("画面の背景色")]
    public Color backgroundColor = new Color(0.55f, 0.78f, 0.98f);
    [Tooltip("バトル背景の画像（空欄なら背景色のみ）")]
    public Sprite backgroundSprite;
    [Tooltip("盤面のマスの色（市松模様の2色）")]
    public Color tileColorA = new Color(0.66f, 0.48f, 0.32f);
    public Color tileColorB = new Color(0.56f, 0.40f, 0.26f);
    [Tooltip("ON：ライトの影響を受けずに画像を本来の明るさで表示（暗く見えるときはON）")]
    public bool unlitSprites = true;

    [Header("ドロップの色（0火 1水 2木 3光 4闇 5回復）")]
    public Color[] orbColors =
    {
        new Color(0.95f, 0.30f, 0.30f), // 火
        new Color(0.30f, 0.55f, 1.00f), // 水
        new Color(0.35f, 0.85f, 0.40f), // 木
        new Color(1.00f, 0.85f, 0.30f), // 光
        new Color(0.70f, 0.40f, 0.95f), // 闇
        new Color(1.00f, 0.50f, 0.75f), // 回復
    };

    [Header("ドロップの画像（設定すると色より優先。配列の長さ = ドロップの種類数）")]
    [Tooltip("火・水・木・光・闇・回復の順に入れる。空欄の要素は色付きの丸で表示")]
    public Sprite[] orbSprites;
    [Tooltip("画像にも色を重ねたい場合はON（通常はOFF）")]
    public bool tintSprites = false;
    [Tooltip("回復ドロップの種類番号")]
    public int heartType = 5;

    [Header("パーティー")]
    public PartyMember[] party =
    {
        new PartyMember { name = "火の剣士",   element = OrbElement.Fire,  attack = 1500, hp = 3200, recovery = 250 },
        new PartyMember { name = "水の魔導士", element = OrbElement.Water, attack = 1300, hp = 2800, recovery = 400 },
        new PartyMember { name = "木の弓使い", element = OrbElement.Wood,  attack = 1400, hp = 3000, recovery = 300 },
        new PartyMember { name = "光の聖騎士", element = OrbElement.Light, attack = 1200, hp = 3800, recovery = 350 },
        new PartyMember { name = "闇の暗殺者", element = OrbElement.Dark,  attack = 1700, hp = 2500, recovery = 200 },
    };

    [Header("ダメージ計算")]
    [Tooltip("1コンボ増えるごとの倍率の上昇量（0.25 → 2コンボで1.25倍、5コンボで2倍）")]
    public float comboBonus = 0.25f;
    [Tooltip("1グループで4個以上消したとき、1個増えるごとの倍率の上昇量")]
    public float extraOrbBonus = 0.25f;

    [Header("敵の種類（ステージのランクに合うものからランダムに出現）")]
    public EnemyData[] enemyTypes =
    {
        new EnemyData { name = "スライム",     rank = EnemyRank.Zako, element = OrbElement.Water, hp = 6000,   attack = 800,  turns = 1 },
        new EnemyData { name = "ゴブリン",     rank = EnemyRank.Zako, element = OrbElement.Wood,  hp = 9000,   attack = 1200, turns = 2 },
        new EnemyData { name = "コウモリ",     rank = EnemyRank.Zako, element = OrbElement.Dark,  hp = 5000,   attack = 1000, turns = 1 },
        new EnemyData { name = "ゴーレム",     rank = EnemyRank.Chu,  element = OrbElement.Light, hp = 30000,  attack = 2500, turns = 2 },
        new EnemyData { name = "炎の魔導士",   rank = EnemyRank.Chu,  element = OrbElement.Fire,  hp = 24000,  attack = 3000, turns = 2 },
        new EnemyData { name = "ドラゴン",     rank = EnemyRank.Boss, element = OrbElement.Fire,  hp = 120000, attack = 6000, turns = 3 },
        new EnemyData { name = "魔王",         rank = EnemyRank.Boss, element = OrbElement.Dark,  hp = 150000, attack = 8000, turns = 3 },
    };

    [Header("ステージ構成")]
    public int totalStages = 10;
    [Tooltip("このステージまで雑魚敵")]
    public int zakoUntilStage = 4;
    [Tooltip("このステージまで中ボス（それ以降はボス）")]
    public int chuUntilStage = 8;
    [Tooltip("雑魚敵の出現数（最小, 最大）")]
    public Vector2Int zakoCount = new Vector2Int(1, 3);
    [Tooltip("中ボスの出現数（最小, 最大）")]
    public Vector2Int chuCount = new Vector2Int(1, 3);
    [Tooltip("ボスの出現数（最小, 最大）")]
    public Vector2Int bossCount = new Vector2Int(1, 2);
    [Tooltip("属性相性を使う（有利2倍・不利0.5倍）")]
    public bool useElementAdvantage = true;
    [Tooltip("敵1体の最大の大きさ（マス数単位）")]
    public float enemySize = 3.5f;

    [Header("スマホ対応")]
    [Tooltip("フレームレート（スマホは標準だと30fpsでドラッグがカクつくので60推奨）")]
    public int targetFrameRate = 60;
    [Tooltip("ON：iPhone のノッチ・ホームバーなどを避けて盤面を配置")]
    public bool useSafeArea = true;
    [Tooltip("タッチ操作のとき、持っているドロップを指より少し上に表示（マス数単位。0 で指の真下）")]
    [Range(0f, 1f)] public float touchOrbOffset = 0f;

    [Header("操作")]
    [Tooltip("最初にドロップを動かしてからの制限時間（秒）")]
    public float moveTimeLimit = 4f;
    [Tooltip("マスの中心からこの距離（セルサイズ比）以内に入ったら入れ替え。小さいほど斜め移動しやすい")]
    [Range(0.2f, 0.5f)] public float swapRadius = 0.42f;

    [Header("演出")]
    public float swapSpeed = 25f;
    public float fallSpeed = 14f;
    public float clearDuration = 0.3f;
    [Range(0.5f, 1f)] public float orbScale = 0.95f;

    // ============================================================
    // 内部データ
    // ============================================================
    enum State { Idle, Dragging, Resolving }

    class Orb
    {
        public int type;
        public Transform tr;
        public SpriteRenderer sr;
        public Vector3 target;
        public float speed;
        public float baseScale;
    }

    class Bar
    {
        public Transform fill, bg;
        public float left, width, height, y;
    }

    class EnemyUnit
    {
        public EnemyData data;
        public long hp, maxHp;
        public int countdown;
        public SpriteRenderer sr;
        public Vector3 basePos;
        public float size;
        public Bar bar;
        public bool Dead => hp <= 0;
    }

    struct ClearedGroup { public int type; public int count; }

    class FloatText
    {
        public string text;
        public Vector3 world;
        public Color color;
        public float time;
        public float sizeMul;
    }

    const string PreviewName = "__PuzzlePreview";
    const string ContentName = "__PuzzleContent";

    Transform root;
    bool isPreview;
    System.Random rng = new System.Random();

    Orb[,] grid;
    State state = State.Idle;
    Camera cam;
    Sprite orbSprite;
    Sprite squareSprite;
    Material unlitMat;
    SpriteRenderer bgRenderer;

    // ドラッグ
    int activeFinger = -1;      // 操作中の指（-1 = なし）
    bool pointerIsTouch;
    Orb heldOrb;
    Vector2Int heldCell;
    bool moveStarted;
    float moveTimer;

    // コンボ
    int comboCount;
    float comboShowTimer;
    readonly List<ClearedGroup> clearedGroups = new List<ClearedGroup>();

    // パーティー
    Vector3[] partyPos = new Vector3[0];
    long teamMaxHp, teamHp;
    Bar teamHpBar;

    // 敵・ステージ
    readonly List<EnemyUnit> enemies = new List<EnemyUnit>();
    int stage = 1;
    int targetIndex = -1;
    bool gameOverHappened;
    bool battleOver;

    // UI
    readonly List<FloatText> floats = new List<FloatText>();
    string bannerText;
    float bannerTimer;
    GUIStyle textStyle;

    int TypeCount => (orbSprites != null && orbSprites.Length > 0) ? orbSprites.Length : orbColors.Length;
    int PartyCount => party == null ? 0 : party.Length;

    // ============================================================
    // レイアウト（盤面の中心 = このオブジェクトの位置）
    // ============================================================
    float BoardHalfW => columns * cellSize * 0.5f;
    float BoardTopY => rows * cellSize * 0.5f;
    float HpBarH => 0.3f * cellSize;
    float HpBarY => BoardTopY + 0.1f * cellSize + HpBarH * 0.5f;
    float IconSize => columns * cellSize / Mathf.Max(1, PartyCount);
    float PartyY => HpBarY + HpBarH * 0.5f + 0.1f * cellSize + IconSize * 0.5f;
    float EnemyY => PartyY + IconSize * 0.5f + 0.8f * cellSize + enemySize * cellSize * 0.5f;
    float EnemyBarY => EnemyY - enemySize * cellSize * 0.5f - 0.3f * cellSize;

    Vector3 W(float x, float y) => transform.position + new Vector3(x, y, 0f);

    Color ElementColor(OrbElement e) => orbColors.Length > 0 ? orbColors[(int)e % orbColors.Length] : Color.white;

    // ============================================================
    // ライフサイクル
    // ============================================================
    void OnEnable()
    {
        if (!Application.isPlaying) BuildAll(true);
    }

    void OnDisable()
    {
        if (!Application.isPlaying) ClearRoot();
    }

#if UNITY_EDITOR
    // Inspector の値を変えたら Scene のプレビューを作り直す
    void OnValidate()
    {
        if (Application.isPlaying) return;
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this == null || !isActiveAndEnabled || Application.isPlaying) return;
            BuildAll(true);
        };
    }
#endif

    void Start()
    {
        if (!Application.isPlaying) return;

        ApplyMobileSettings(targetFrameRate);
        SetupCamera();
        if (autoStart) StartBattle(null);
    }

    /// <summary>スマホ向けの共通設定（GameFlow からも呼ばれる）</summary>
    public static void ApplyMobileSettings(int fps)
    {
        Application.targetFrameRate = fps;
        QualitySettings.vSyncCount = 0;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;            // プレイ中に画面が暗くならない
        if (Application.isMobilePlatform)
        {
            Screen.orientation = ScreenOrientation.Portrait;        // 縦画面に固定
#if ENABLE_LEGACY_INPUT_MANAGER
            Input.multiTouchEnabled = false;                        // 2本目の指を無視
#endif
        }
    }

    // アプリが裏に回ったら、持っているドロップを放す
    void OnApplicationPause(bool paused)
    {
        if (paused && state == State.Dragging) Release();
    }

    void SetupCamera()
    {
        if (cam != null) return;
        cam = Camera.main;
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
    }

    /// <summary>バトルを最初（ステージ1）から開始。newParty を渡すとそのパーティーで戦う</summary>
    public void StartBattle(PartyMember[] newParty)
    {
        if (!Application.isPlaying) return;
        if (newParty != null && newParty.Length > 0) party = newParty;

        StopAllCoroutines();
        state = State.Idle;
        heldOrb = null;
        battleOver = false;
        inputLocked = false;
        floats.Clear();
        comboShowTimer = 0f;
        bannerTimer = 0f;

        SetupCamera();
        BuildAll(false);
        UpdateCamera();
        StartCoroutine(StageIntro());
    }

    /// <summary>バトルを終了して盤面などを消す</summary>
    public void StopBattle()
    {
        StopAllCoroutines();
        battleOver = true;
        heldOrb = null;
        floats.Clear();
        ClearRoot();
    }

    void Update()
    {
        if (!Application.isPlaying || root == null) return;

        UpdateCamera();
        HandleInput();
        AnimateOrbs();

        if (comboShowTimer > 0f && state != State.Resolving) comboShowTimer -= Time.deltaTime;
        if (bannerTimer > 0f) bannerTimer -= Time.deltaTime;

        for (int i = floats.Count - 1; i >= 0; i--)
        {
            floats[i].time += Time.deltaTime;
            if (floats[i].time > 1.2f) floats.RemoveAt(i);
        }
    }

    void UpdateCamera()
    {
        cam.backgroundColor = backgroundColor;
        float halfW = BoardHalfW + sideMargin * cellSize;
        float halfH = BoardTopY;

        // セーフエリア（ホームバー・ノッチ）の分だけ余白を足す
        Rect safe = useSafeArea ? Screen.safeArea : new Rect(0, 0, Screen.width, Screen.height);
        float sw = Mathf.Max(1f, Screen.width), sh = Mathf.Max(1f, Screen.height);
        float bottomInset = Mathf.Clamp01(safe.yMin / sh);                 // 画面下の隠れる割合
        float sideInset = Mathf.Clamp01(Mathf.Max(safe.xMin, sw - safe.xMax) / sw);

        if (boardAtBottom)
        {
            // 横幅：左右の隠れる部分を除いた幅に盤面が収まるように
            float usableAspect = cam.aspect * (1f - 2f * sideInset);
            float size = Mathf.Max(halfW / Mathf.Max(0.1f, usableAspect), halfH + bottomMargin * cellSize);
            cam.orthographicSize = size;
            float insetWorld = bottomInset * size * 2f;
            float boardBottom = -halfH - bottomMargin * cellSize - insetWorld;
            cam.transform.position = W(0f, boardBottom + size) + new Vector3(0f, 0f, -10f);
        }
        else
        {
            float top = EnemyY + enemySize * cellSize * 0.5f + 0.5f * cellSize;
            float bottom = -halfH - bottomMargin * cellSize;
            float size = Mathf.Max((top - bottom) * 0.5f, halfW / cam.aspect);
            cam.orthographicSize = size;
            cam.transform.position = W(0f, (top + bottom) * 0.5f) + new Vector3(0f, 0f, -10f);
        }

        // 背景画像を画面いっぱいに
        if (bgRenderer != null && bgRenderer.sprite != null)
        {
            float viewH = cam.orthographicSize * 2f;
            CoverSprite(bgRenderer, new Vector3(cam.transform.position.x, cam.transform.position.y, 0f), viewH * cam.aspect, viewH);
        }
    }

    void CoverSprite(SpriteRenderer sr, Vector3 center, float w, float h)
    {
        Vector2 b = sr.sprite.bounds.size;
        float sc = Mathf.Max(w / Mathf.Max(0.0001f, b.x), h / Mathf.Max(0.0001f, b.y));
        sr.transform.position = center;
        sr.transform.localScale = Vector3.one * sc;
    }

    // ============================================================
    // 生成
    // ============================================================
    void ClearRoot()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform c = transform.GetChild(i);
            if (c.name == PreviewName || c.name == ContentName) DestroyImmediate(c.gameObject);
        }
        root = null;
    }

    void BuildAll(bool preview)
    {
        ClearRoot();
        isPreview = preview;
        rng = preview ? new System.Random(12345) : new System.Random();
        EnsureSprites();

        root = NewGO(preview ? PreviewName : ContentName, transform).transform;
        root.localPosition = Vector3.zero;

        BuildBackground();
        BuildTiles();
        BuildOrbs();
        BuildTeam();
        BuildEnemy();
    }

    GameObject NewGO(string name, Transform parent)
    {
        var go = new GameObject(name);
        // Scene用のプレビューはシーンに保存しない
        if (isPreview) go.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        go.transform.SetParent(parent, false);
        return go;
    }

    void EnsureSprites()
    {
        if (unlitMat == null)
        {
            // URP の 2D 用 → 標準パイプライン用 の順に、ライトの影響を受けないシェーダーを探す
            Shader sh = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null) unlitMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }
        if (orbSprite == null) orbSprite = CreateOrbSprite(128);
        if (squareSprite == null)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            tex.SetPixels(px);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            squareSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            squareSprite.hideFlags = HideFlags.HideAndDontSave;
        }
    }

    /// <summary>白い球体風の画像を生成（色はSpriteRendererで着色）</summary>
    Sprite CreateOrbSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.hideFlags = HideFlags.HideAndDontSave;

        var px = new Color[size * size];
        float r = size * 0.5f;
        var center = new Vector2(r, r);
        var highlight = new Vector2(size * 0.36f, size * 0.66f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Vector2.Distance(p, center) / r;
                float alpha = Mathf.Clamp01((1f - d) * r * 0.5f);
                float shade = Mathf.Lerp(1f, 0.55f, d * d);
                float h = Mathf.Clamp01(1f - Vector2.Distance(p, highlight) / (size * 0.22f));
                float v = Mathf.Clamp01(shade + h * h * 0.6f);
                px[y * size + x] = new Color(v, v, v, alpha);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    static float FitScale(Sprite s, float size)
    {
        if (s == null) return size;
        Vector2 b = s.bounds.size;
        float m = Mathf.Max(b.x, b.y);
        return size / (m > 0f ? m : 1f);
    }

    SpriteRenderer MakeSprite(string name, Sprite sprite, Vector3 pos, float size, Color color, int order)
    {
        var go = NewGO(name, root);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * FitScale(sprite, size);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = order;
        if (unlitSprites && unlitMat != null) sr.sharedMaterial = unlitMat;
        return sr;
    }

    SpriteRenderer MakeRect(string name, Vector3 center, float w, float h, Color color, int order)
    {
        var go = NewGO(name, root);
        go.transform.position = center;
        go.transform.localScale = new Vector3(w, h, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = squareSprite;
        sr.color = color;
        sr.sortingOrder = order;
        if (unlitSprites && unlitMat != null) sr.sharedMaterial = unlitMat;
        return sr;
    }

    Bar MakeBar(string name, float x, float y, float width, float height, Color bg, Color fg)
    {
        var back = MakeRect(name + "_BG", W(x, y), width + 0.06f * cellSize, height + 0.06f * cellSize, bg, 20);
        var fill = MakeRect(name + "_Fill", W(x, y), width, height, fg, 21);
        return new Bar { fill = fill.transform, bg = back.transform, left = x - width * 0.5f, width = width, height = height, y = y };
    }

    void SetBar(Bar b, float ratio)
    {
        if (b == null) return;
        ratio = Mathf.Clamp01(ratio);
        b.fill.position = W(b.left + b.width * ratio * 0.5f, b.y);
        b.fill.localScale = new Vector3(Mathf.Max(0.0001f, b.width * ratio), b.height, 1f);
    }

    void BuildBackground()
    {
        bgRenderer = null;
        if (backgroundSprite == null) return;
        bgRenderer = MakeSprite("Background", backgroundSprite, W(0f, 0f), 1f, Color.white, -100);
        // Scene用：盤面から敵のエリアまでを覆う
        float bottom = -BoardTopY - bottomMargin * cellSize;
        float top = EnemyY + enemySize * cellSize * 0.5f + cellSize;
        CoverSprite(bgRenderer, W(0f, (top + bottom) * 0.5f), BoardHalfW * 2f + 2f * cellSize, top - bottom);
    }

    void BuildTiles()
    {
        for (int x = 0; x < columns; x++)
            for (int y = 0; y < rows; y++)
            {
                Color c = ((x + y) % 2 == 0) ? tileColorA : tileColorB;
                MakeRect($"Tile_{x}_{y}", CellToWorld(x, y), cellSize, cellSize, c, -10);
            }
    }

    void BuildOrbs()
    {
        grid = new Orb[columns, rows];
        for (int x = 0; x < columns; x++)
            for (int y = 0; y < rows; y++)
                grid[x, y] = CreateOrb(RandomTypeWithoutMatch(x, y), CellToWorld(x, y));
    }

    void BuildTeam()
    {
        int n = PartyCount;
        partyPos = new Vector3[n];
        teamMaxHp = 0;

        for (int i = 0; i < n; i++)
        {
            PartyMember m = party[i];
            Vector3 p = W(-BoardHalfW + IconSize * (i + 0.5f), PartyY);
            partyPos[i] = p;
            if (m == null) continue;
            teamMaxHp += m.hp;

            float s = IconSize * 0.92f;
            Color col = ElementColor(m.element);
            if (m.icon != null)
            {
                MakeSprite($"Member{i}", m.icon, p, s, Color.white, 5);
            }
            else
            {
                // 仮アイコン：属性色の枠＋属性マーク
                MakeRect($"Member{i}_Frame", p, s, s, col * 0.8f, 4);
                MakeRect($"Member{i}_BG", p, s * 0.88f, s * 0.88f, new Color(0.15f, 0.12f, 0.18f), 5);
                MakeSprite($"Member{i}_Mark", orbSprite, p, s * 0.55f, col, 6);
            }
        }

        teamHp = teamMaxHp;
        teamHpBar = MakeBar("TeamHP", 0f, HpBarY, BoardHalfW * 2f - 0.2f * cellSize, HpBarH,
                            new Color(0.12f, 0.05f, 0.09f), new Color(1f, 0.4f, 0.7f));
        SetBar(teamHpBar, 1f);
    }

    void BuildEnemy()
    {
        stage = 1;
        SpawnStage(stage);
    }

    static void DestroyObj(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    EnemyRank RankOfStage(int s) =>
        s <= zakoUntilStage ? EnemyRank.Zako : s <= chuUntilStage ? EnemyRank.Chu : EnemyRank.Boss;

    /// <summary>ステージの敵をランダムに出現させる</summary>
    void SpawnStage(int s)
    {
        // 前のステージの敵を片付ける
        foreach (var e in enemies)
        {
            if (e.sr != null) DestroyObj(e.sr.gameObject);
            if (e.bar != null) { DestroyObj(e.bar.fill.gameObject); DestroyObj(e.bar.bg.gameObject); }
        }
        enemies.Clear();
        targetIndex = -1;

        EnemyRank rank = RankOfStage(s);
        var pool = new List<EnemyData>();
        if (enemyTypes != null)
            foreach (var d in enemyTypes)
                if (d != null && d.rank == rank) pool.Add(d);
        if (pool.Count == 0 && enemyTypes != null)          // そのランクがいなければ全種類から
            foreach (var d in enemyTypes) if (d != null) pool.Add(d);
        if (pool.Count == 0) return;

        Vector2Int range = rank == EnemyRank.Zako ? zakoCount : rank == EnemyRank.Chu ? chuCount : bossCount;
        int min = Mathf.Max(1, Mathf.Min(range.x, range.y));
        int max = Mathf.Max(min, Mathf.Max(range.x, range.y));
        int count = rng.Next(min, max + 1);

        float slotW = BoardHalfW * 2f / count;
        float size = Mathf.Min(enemySize * cellSize, slotW * 0.9f);

        for (int i = 0; i < count; i++)
        {
            EnemyData d = pool[rng.Next(pool.Count)];
            float x = -BoardHalfW + slotW * (i + 0.5f);
            var pos = W(x, EnemyY);

            SpriteRenderer sr = d.sprite != null
                ? MakeSprite($"Enemy{i}", d.sprite, pos, size, Color.white, 0)
                : MakeSprite($"Enemy{i}", orbSprite, pos, size, Color.Lerp(ElementColor(d.element), Color.black, 0.15f), 0);

            var unit = new EnemyUnit
            {
                data = d,
                hp = d.hp,
                maxHp = d.hp,
                countdown = Mathf.Max(1, d.turns),
                sr = sr,
                basePos = pos,
                size = size,
                bar = MakeBar($"Enemy{i}HP", x, EnemyBarY, slotW * 0.8f, 0.16f * cellSize,
                              new Color(0.1f, 0.1f, 0.1f), new Color(0.85f, 0.9f, 0.25f)),
            };
            SetBar(unit.bar, 1f);
            enemies.Add(unit);
        }
    }

    EnemyUnit CurrentTarget()
    {
        if (targetIndex >= 0 && targetIndex < enemies.Count && !enemies[targetIndex].Dead) return enemies[targetIndex];
        targetIndex = -1;
        foreach (var e in enemies) if (!e.Dead) return e;
        return null;
    }

    bool AllEnemiesDead()
    {
        foreach (var e in enemies) if (!e.Dead) return false;
        return true;
    }

    /// <summary>属性相性：火→木→水→火 は2倍（逆は0.5倍）、光⇔闇 は互いに2倍</summary>
    float Advantage(OrbElement atk, OrbElement def)
    {
        if (!useElementAdvantage) return 1f;
        if ((atk == OrbElement.Fire && def == OrbElement.Wood) ||
            (atk == OrbElement.Wood && def == OrbElement.Water) ||
            (atk == OrbElement.Water && def == OrbElement.Fire)) return 2f;
        if ((atk == OrbElement.Wood && def == OrbElement.Fire) ||
            (atk == OrbElement.Water && def == OrbElement.Wood) ||
            (atk == OrbElement.Fire && def == OrbElement.Water)) return 0.5f;
        if ((atk == OrbElement.Light && def == OrbElement.Dark) ||
            (atk == OrbElement.Dark && def == OrbElement.Light)) return 2f;
        return 1f;
    }

    Orb CreateOrb(int type, Vector3 pos)
    {
        Sprite custom = (orbSprites != null && type < orbSprites.Length) ? orbSprites[type] : null;
        Color col = orbColors.Length > 0 ? orbColors[type % orbColors.Length] : Color.white;

        SpriteRenderer sr = custom != null
            ? MakeSprite("Orb", custom, pos, cellSize * orbScale, tintSprites ? col : Color.white, 0)
            : MakeSprite("Orb", orbSprite, pos, cellSize * orbScale, col, 0);

        return new Orb
        {
            type = type,
            tr = sr.transform,
            sr = sr,
            target = pos,
            speed = fallSpeed,
            baseScale = sr.transform.localScale.x
        };
    }

    int RandomTypeWithoutMatch(int x, int y)
    {
        for (int tries = 0; tries < 100; tries++)
        {
            int t = rng.Next(TypeCount);
            if (x >= 2 && grid[x - 1, y].type == t && grid[x - 2, y].type == t) continue;
            if (y >= 2 && grid[x, y - 1].type == t && grid[x, y - 2].type == t) continue;
            return t;
        }
        return rng.Next(TypeCount);
    }

    // ============================================================
    // 座標変換
    // ============================================================
    Vector3 CellToWorld(int x, int y) =>
        W((x - (columns - 1) * 0.5f) * cellSize, (y - (rows - 1) * 0.5f) * cellSize);

    Vector3 CellToWorld(Vector2Int c) => CellToWorld(c.x, c.y);

    Vector2Int WorldToCell(Vector3 world)
    {
        Vector3 local = world - transform.position;
        int x = Mathf.RoundToInt(local.x / cellSize + (columns - 1) * 0.5f);
        int y = Mathf.RoundToInt(local.y / cellSize + (rows - 1) * 0.5f);
        return new Vector2Int(Mathf.Clamp(x, 0, columns - 1), Mathf.Clamp(y, 0, rows - 1));
    }

    // ============================================================
    // 入力・ドラッグ
    // ============================================================
    /// <summary>
    /// マウス・タッチ共通の入力読み取り。
    /// タッチは最初に触れた1本の指だけを追いかける（2本目の指では操作が乱れない）
    /// </summary>
    bool ReadPointer(out Vector2 pos, out bool down, out bool held, out int pointerId)
    {
        pos = default; down = held = false; pointerId = -1;
#if ENABLE_INPUT_SYSTEM
        var ts = Touchscreen.current;
        if (ts != null && (ts.primaryTouch.press.isPressed || ts.primaryTouch.press.wasReleasedThisFrame || Application.isMobilePlatform))
        {
            var t = ts.primaryTouch;                               // 最初に触れた指
            pos = t.position.ReadValue();
            down = t.press.wasPressedThisFrame;
            held = t.press.isPressed;
            pointerId = t.touchId.ReadValue();
            pointerIsTouch = true;
            return true;
        }
        var p = Pointer.current;
        if (p == null) return false;
        pos = p.position.ReadValue();
        down = p.press.wasPressedThisFrame;
        held = p.press.isPressed;
        pointerIsTouch = false;
        return true;
#else
        if (Input.touchCount > 0 || activeFinger >= 0)
        {
            // 操作中の指を探す
            if (activeFinger >= 0)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch t = Input.GetTouch(i);
                    if (t.fingerId != activeFinger) continue;
                    pos = t.position;
                    held = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
                    pointerId = t.fingerId;
                    if (!held) activeFinger = -1;
                    pointerIsTouch = true;
                    return true;
                }
                activeFinger = -1;                                 // 指が見つからない = 離された
                pointerIsTouch = true;
                return true;                                       // held = false
            }
            // 新しく触れた指
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.phase != TouchPhase.Began) continue;
                activeFinger = t.fingerId;
                pos = t.position;
                down = held = true;
                pointerId = t.fingerId;
                pointerIsTouch = true;
                return true;
            }
            pointerIsTouch = true;
            return true;                                           // 触れているが操作対象の指ではない
        }
        pos = Input.mousePosition;
        down = Input.GetMouseButtonDown(0);
        held = Input.GetMouseButton(0);
        pointerIsTouch = false;
        return true;
#endif
    }

    readonly List<UnityEngine.EventSystems.RaycastResult> uiHits = new List<UnityEngine.EventSystems.RaycastResult>();

    /// <summary>その位置にボタンなどの UI があるか（タッチした最初のフレームでも正しく判定できるよう直接調べる）</summary>
    bool IsOverUI(Vector2 screenPos)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return false;
        var data = new UnityEngine.EventSystems.PointerEventData(es) { position = screenPos };
        uiHits.Clear();
        es.RaycastAll(data, uiHits);
        return uiHits.Count > 0;
    }

    void HandleInput()
    {
        if (state == State.Resolving || inputLocked || battleOver) return;

        if (!ReadPointer(out Vector2 screenPos, out bool down, out bool held, out int pointerId))
        {
            if (state == State.Dragging) Release();
            return;
        }

        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 10f));
        world.z = 0f;

        if (state == State.Idle)
        {
            // ボタンなどの UI の上をタッチしたときは盤面を操作しない
            if (down && IsOverUI(screenPos)) return;
            if (down && !TryPick(world)) TrySelectTarget(world);
            return;
        }

        if (!held) { Release(); return; }

        // タッチのときは指で隠れないよう少し上に表示（設定時のみ）
        if (pointerIsTouch && touchOrbOffset > 0f) world.y += touchOrbOffset * cellSize;
        DragTo(world);

        if (moveStarted)
        {
            moveTimer -= Time.deltaTime;
            if (moveTimer <= 0f) Release();
        }
    }

    void TrySelectTarget(Vector3 world)
    {
        for (int i = 0; i < enemies.Count; i++)
        {
            var e = enemies[i];
            if (e.Dead) continue;
            if (Mathf.Abs(world.x - e.basePos.x) < e.size * 0.5f && Mathf.Abs(world.y - e.basePos.y) < e.size * 0.5f)
            {
                targetIndex = (targetIndex == i) ? -1 : i;   // もう一度タップで解除
                return;
            }
        }
    }

    bool TryPick(Vector3 world)
    {
        Vector3 local = world - transform.position;
        if (Mathf.Abs(local.x) > BoardHalfW || Mathf.Abs(local.y) > BoardTopY) return false;

        Vector2Int c = WorldToCell(world);
        heldOrb = grid[c.x, c.y];
        if (heldOrb == null) return false;

        heldCell = c;
        moveStarted = false;
        state = State.Dragging;

        heldOrb.sr.sortingOrder = 10;
        // タッチは指で隠れるので少し大きめに
        heldOrb.tr.localScale = Vector3.one * heldOrb.baseScale * (pointerIsTouch ? 1.3f : 1.15f);
        var col = heldOrb.sr.color; col.a = 0.85f; heldOrb.sr.color = col;
        return true;
    }

    void DragTo(Vector3 world)
    {
        Vector3 local = world - transform.position;
        float maxX = (columns - 1) * 0.5f * cellSize;
        float maxY = (rows - 1) * 0.5f * cellSize;
        local.x = Mathf.Clamp(local.x, -maxX, maxX);
        local.y = Mathf.Clamp(local.y, -maxY, maxY);
        Vector3 pos = transform.position + local;
        heldOrb.tr.position = pos;

        Vector2Int target = WorldToCell(pos);
        if (target == heldCell) return;
        if ((CellToWorld(target) - pos).magnitude > cellSize * swapRadius) return;

        while (heldCell != target)
        {
            var step = new Vector2Int(System.Math.Sign(target.x - heldCell.x), System.Math.Sign(target.y - heldCell.y));
            SwapHeldInto(heldCell + step);
        }
    }

    void SwapHeldInto(Vector2Int next)
    {
        Orb other = grid[next.x, next.y];
        grid[heldCell.x, heldCell.y] = other;
        if (other != null)
        {
            other.target = CellToWorld(heldCell);
            other.speed = swapSpeed;
        }
        grid[next.x, next.y] = heldOrb;
        heldCell = next;

        if (!moveStarted)
        {
            moveStarted = true;
            moveTimer = moveTimeLimit;
        }
    }

    void Release()
    {
        heldOrb.sr.sortingOrder = 0;
        heldOrb.tr.localScale = Vector3.one * heldOrb.baseScale;
        var col = heldOrb.sr.color; col.a = 1f; heldOrb.sr.color = col;
        heldOrb.target = CellToWorld(heldCell);
        heldOrb.speed = swapSpeed;
        heldOrb = null;

        if (moveStarted) StartCoroutine(Resolve());
        else state = State.Idle;
    }

    void AnimateOrbs()
    {
        for (int x = 0; x < columns; x++)
            for (int y = 0; y < rows; y++)
            {
                Orb o = grid[x, y];
                if (o == null || o == heldOrb) continue;
                o.tr.position = Vector3.MoveTowards(o.tr.position, o.target, o.speed * Time.deltaTime);
            }
    }

    bool AllSettled()
    {
        foreach (Orb o in grid)
            if (o != null && o.tr.position != o.target) return false;
        return true;
    }

    // ============================================================
    // 消去・落下・連鎖
    // ============================================================
    IEnumerator Resolve()
    {
        state = State.Resolving;
        comboCount = 0;
        clearedGroups.Clear();

        yield return new WaitUntil(AllSettled);

        while (true)
        {
            List<List<Vector2Int>> groups = FindMatchGroups();
            if (groups.Count == 0) break;

            foreach (var g in groups)
            {
                comboCount++;
                comboShowTimer = 1.5f;
                clearedGroups.Add(new ClearedGroup { type = grid[g[0].x, g[0].y].type, count = g.Count });
                yield return StartCoroutine(ClearGroup(g));
            }

            ApplyGravityAndRefill();
            yield return new WaitUntil(AllSettled);
        }

        yield return StartCoroutine(BattlePhase());
        if (!battleOver) state = State.Idle;
    }

    List<List<Vector2Int>> FindMatchGroups()
    {
        var matched = new bool[columns, rows];

        for (int y = 0; y < rows; y++)
        {
            int x = 0;
            while (x < columns)
            {
                if (grid[x, y] == null) { x++; continue; }
                int t = grid[x, y].type;
                int end = x + 1;
                while (end < columns && grid[end, y] != null && grid[end, y].type == t) end++;
                if (end - x >= 3)
                    for (int i = x; i < end; i++) matched[i, y] = true;
                x = end;
            }
        }

        for (int x = 0; x < columns; x++)
        {
            int y = 0;
            while (y < rows)
            {
                if (grid[x, y] == null) { y++; continue; }
                int t = grid[x, y].type;
                int end = y + 1;
                while (end < rows && grid[x, end] != null && grid[x, end].type == t) end++;
                if (end - y >= 3)
                    for (int i = y; i < end; i++) matched[x, i] = true;
                y = end;
            }
        }

        var groups = new List<List<Vector2Int>>();
        var visited = new bool[columns, rows];
        var dirs = new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

        for (int x = 0; x < columns; x++)
            for (int y = 0; y < rows; y++)
            {
                if (!matched[x, y] || visited[x, y]) continue;

                int t = grid[x, y].type;
                var group = new List<Vector2Int>();
                var queue = new Queue<Vector2Int>();
                queue.Enqueue(new Vector2Int(x, y));
                visited[x, y] = true;

                while (queue.Count > 0)
                {
                    Vector2Int c = queue.Dequeue();
                    group.Add(c);
                    foreach (var d in dirs)
                    {
                        Vector2Int n = c + d;
                        if (n.x < 0 || n.x >= columns || n.y < 0 || n.y >= rows) continue;
                        if (visited[n.x, n.y] || !matched[n.x, n.y]) continue;
                        if (grid[n.x, n.y].type != t) continue;
                        visited[n.x, n.y] = true;
                        queue.Enqueue(n);
                    }
                }
                groups.Add(group);
            }
        return groups;
    }

    IEnumerator ClearGroup(List<Vector2Int> cells)
    {
        var orbs = new List<Orb>();
        foreach (var c in cells)
        {
            orbs.Add(grid[c.x, c.y]);
            grid[c.x, c.y] = null;
        }

        float t = 0f;
        while (t < clearDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / clearDuration);
            foreach (var o in orbs)
            {
                o.tr.localScale = Vector3.one * o.baseScale * (1f + k * 0.3f);
                var col = o.sr.color; col.a = 1f - k; o.sr.color = col;
            }
            yield return null;
        }

        foreach (var o in orbs) Destroy(o.tr.gameObject);
    }

    void ApplyGravityAndRefill()
    {
        for (int x = 0; x < columns; x++)
        {
            int write = 0;
            for (int y = 0; y < rows; y++)
            {
                Orb o = grid[x, y];
                if (o == null) continue;
                if (y != write)
                {
                    grid[x, write] = o;
                    grid[x, y] = null;
                    o.target = CellToWorld(x, write);
                    o.speed = fallSpeed;
                }
                write++;
            }

            int spawned = 0;
            for (int y = write; y < rows; y++)
            {
                Orb o = CreateOrb(rng.Next(TypeCount), CellToWorld(x, rows + spawned));
                o.target = CellToWorld(x, y);
                o.speed = fallSpeed;
                grid[x, y] = o;
                spawned++;
            }
        }
    }

    // ============================================================
    // バトル（コンボ倍率つき攻撃・回復・敵の攻撃）
    // ============================================================
    IEnumerator BattlePhase()
    {
        int n = PartyCount;
        float comboMul = comboCount > 0 ? 1f + (comboCount - 1) * comboBonus : 1f;

        // --- ダメージと回復量を計算 ---
        var damage = new double[n];
        double heal = 0;
        long totalRecovery = 0;
        for (int i = 0; i < n; i++) if (party[i] != null) totalRecovery += party[i].recovery;

        foreach (var g in clearedGroups)
        {
            float groupMul = 1f + Mathf.Max(0, g.count - 3) * extraOrbBonus;   // 4個消し・5個消しのボーナス
            if (g.type == heartType)
            {
                heal += totalRecovery * groupMul;
                continue;
            }
            for (int i = 0; i < n; i++)
                if (party[i] != null && (int)party[i].element == g.type)
                    damage[i] += party[i].attack * groupMul;
        }

        // --- 各キャラの攻撃力を表示（コンボ倍率込み） ---
        var finalDamage = new long[n];
        bool anyAttack = false;
        for (int i = 0; i < n; i++)
        {
            finalDamage[i] = (long)(damage[i] * comboMul);
            if (finalDamage[i] <= 0) continue;
            anyAttack = true;
            AddFloat(finalDamage[i].ToString("N0"), partyPos[i] + Vector3.up * IconSize * 0.4f,
                     ElementColor(party[i].element), 0.9f);
        }

        if (anyAttack)
        {
            yield return new WaitForSeconds(0.45f);

            // --- 敵にダメージ（ターゲット優先、倒れていたら次の敵へ） ---
            for (int i = 0; i < n; i++)
            {
                if (finalDamage[i] <= 0) continue;
                EnemyUnit target = CurrentTarget();
                if (target == null) break;

                float adv = Advantage(party[i].element, target.data.element);
                long dmg = (long)(finalDamage[i] * adv);
                target.hp = System.Math.Max(0, target.hp - dmg);
                SetBar(target.bar, (float)target.hp / target.maxHp);

                Vector3 jitter = new Vector3(Random.Range(-0.4f, 0.4f) * target.size, Random.Range(-0.3f, 0.3f) * cellSize, 0f);
                AddFloat(dmg.ToString("N0"), target.basePos + jitter, ElementColor(party[i].element), adv > 1f ? 1.5f : adv < 1f ? 0.9f : 1.2f);
                StartCoroutine(Shake(target.sr.transform, target.basePos, 0.2f, 0.12f * cellSize));
                if (target.Dead)
                {
                    StartCoroutine(FadeOutEnemy(target));
                    int drops = onEnemyDefeated != null ? onEnemyDefeated(target.data) : 0;
                    if (drops > 0)
                        AddFloat(drops > 1 ? $"DROP x{drops}" : "DROP!", target.basePos + Vector3.down * target.size * 0.25f,
                                 new Color(1f, 0.85f, 0.25f), 1.1f);
                }
                yield return new WaitForSeconds(0.18f);
            }
        }

        // --- 回復 ---
        long healAmount = (long)(heal * comboMul);
        if (healAmount > 0)
        {
            teamHp = System.Math.Min(teamMaxHp, teamHp + healAmount);
            SetBar(teamHpBar, (float)teamHp / teamMaxHp);
            AddFloat("+" + healAmount.ToString("N0"), W(0f, HpBarY + 0.3f * cellSize), new Color(0.5f, 1f, 0.6f), 1f);
            yield return new WaitForSeconds(0.35f);
        }

        // --- 全滅させたら次のステージ、残っていれば敵の攻撃 ---
        if (AllEnemiesDead())
        {
            yield return new WaitForSeconds(0.5f);
            yield return StartCoroutine(StageClear());
            yield break;
        }

        gameOverHappened = false;
        foreach (var e in enemies.ToArray())
        {
            if (e.Dead) continue;
            e.countdown--;
            if (e.countdown > 0) continue;
            e.countdown = Mathf.Max(1, e.data.turns);
            yield return StartCoroutine(EnemyAttack(e));
            if (gameOverHappened) yield break;   // やり直し済み
        }
    }

    IEnumerator EnemyAttack(EnemyUnit e)
    {
        // 敵が少し大きくなって攻撃
        Transform t = e.sr.transform;
        Vector3 baseScale = t.localScale;
        for (float k = 0; k < 0.15f; k += Time.deltaTime) { t.localScale = baseScale * (1f + k); yield return null; }
        t.localScale = baseScale;

        int atk = e.data.attack;
        teamHp = System.Math.Max(0, teamHp - atk);
        SetBar(teamHpBar, teamMaxHp > 0 ? (float)teamHp / teamMaxHp : 0f);
        AddFloat("-" + atk.ToString("N0"), W(0f, HpBarY + 0.3f * cellSize), new Color(1f, 0.35f, 0.35f), 1.1f);
        StartCoroutine(Shake(root, transform.position, 0.25f, 0.08f * cellSize));
        yield return new WaitForSeconds(0.45f);

        if (teamHp <= 0)
        {
            gameOverHappened = true;
            bannerText = "GAME OVER";
            bannerTimer = 2f;
            yield return new WaitForSeconds(2f);
            if (onGameOver != null)
            {
                battleOver = true;
                onGameOver();
                yield break;
            }
            // ステージ1からやり直し
            teamHp = teamMaxHp;
            SetBar(teamHpBar, 1f);
            stage = 1;
            SpawnStage(stage);
            yield return StartCoroutine(StageIntro());
        }
    }

    IEnumerator StageClear()
    {
        onStageClear?.Invoke(stage);

        if (stage >= totalStages)
        {
            bannerText = "ALL CLEAR!";
            bannerTimer = 2.5f;
            if (onAllClear != null)
            {
                battleOver = true;
                yield return new WaitForSeconds(1.5f);
                onAllClear();
                yield break;
            }
            yield return new WaitForSeconds(2.5f);
            stage = 1;
            teamHp = teamMaxHp;
            SetBar(teamHpBar, 1f);
        }
        else
        {
            bannerText = $"STAGE {stage} CLEAR";
            bannerTimer = 1.2f;
            yield return new WaitForSeconds(1.2f);
            stage++;
        }

        SpawnStage(stage);
        yield return StartCoroutine(StageIntro());
    }

    /// <summary>ステージ開始の表示と敵のフェードイン</summary>
    IEnumerator StageIntro()
    {
        EnemyRank rank = RankOfStage(stage);
        bannerText = rank == EnemyRank.Boss ? $"BOSS STAGE {stage}" : $"STAGE {stage}";
        bannerTimer = 1.2f;

        var baseColors = new Color[enemies.Count];
        for (int i = 0; i < enemies.Count; i++) baseColors[i] = enemies[i].sr.color;

        for (float k = 0; k < 0.5f; k += Time.deltaTime)
        {
            for (int i = 0; i < enemies.Count; i++)
            {
                var c = baseColors[i]; c.a = k / 0.5f; enemies[i].sr.color = c;
            }
            yield return null;
        }
        for (int i = 0; i < enemies.Count; i++) enemies[i].sr.color = baseColors[i];
        yield return new WaitForSeconds(0.4f);
    }

    IEnumerator FadeOutEnemy(EnemyUnit e)
    {
        yield return new WaitForSeconds(0.2f);
        Color baseColor = e.sr.color;
        for (float k = 0; k < 0.4f; k += Time.deltaTime)
        {
            if (e.sr == null) yield break;
            var c = baseColor; c.a = 1f - k / 0.4f; e.sr.color = c;
            yield return null;
        }
        if (e.sr == null) yield break;
        e.sr.gameObject.SetActive(false);
        e.bar.fill.gameObject.SetActive(false);
        e.bar.bg.gameObject.SetActive(false);
    }

    IEnumerator Shake(Transform t, Vector3 basePos, float duration, float strength)
    {
        for (float k = 0; k < duration; k += Time.deltaTime)
        {
            if (t == null) yield break;
            t.position = basePos + (Vector3)(Random.insideUnitCircle * strength);
            yield return null;
        }
        if (t != null) t.position = basePos;
    }

    void AddFloat(string text, Vector3 world, Color color, float sizeMul)
    {
        floats.Add(new FloatText { text = text, world = world, color = color, sizeMul = sizeMul });
    }

    // ============================================================
    // 文字・バー表示（OnGUI）
    // ============================================================
    Vector2 GuiPos(Vector3 world)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        return new Vector2(sp.x, Screen.height - sp.y);
    }

    void DrawText(Rect r, string text, int size, Color color, TextAnchor anchor)
    {
        textStyle.fontSize = size;
        textStyle.alignment = anchor;
        textStyle.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.85f);
        float o = Mathf.Max(1f, size * 0.06f);
        GUI.Label(new Rect(r.x - o, r.y, r.width, r.height), text, textStyle);
        GUI.Label(new Rect(r.x + o, r.y, r.width, r.height), text, textStyle);
        GUI.Label(new Rect(r.x, r.y - o, r.width, r.height), text, textStyle);
        GUI.Label(new Rect(r.x, r.y + o, r.width, r.height), text, textStyle);
        textStyle.normal.textColor = color;
        GUI.Label(r, text, textStyle);
    }

    void OnGUI()
    {
        if (!Application.isPlaying || cam == null || root == null) return;

        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Overflow };
        }

        int baseFont = Mathf.RoundToInt(Mathf.Min(Screen.width * 0.045f, Screen.height * 0.03f));

        // --- ステージ表示 ---
        Vector2 areaTop = GuiPos(W(0f, EnemyY + enemySize * cellSize * 0.5f + 0.5f * cellSize));
        float safeTop = useSafeArea ? Screen.height - Screen.safeArea.yMax : 0f;      // ノッチの高さ
        float stageY = Mathf.Max(areaTop.y - baseFont * 1.6f, safeTop + baseFont * 0.3f);
        DrawText(new Rect(0, stageY, Screen.width, baseFont * 1.5f),
                 $"STAGE {stage} / {totalStages}", baseFont, Color.white, TextAnchor.MiddleCenter);

        // --- 敵ごとの名前・HP・攻撃までのターン・ターゲット ---
        int small = Mathf.RoundToInt(baseFont * (enemies.Count >= 3 ? 0.6f : 0.75f));
        EnemyUnit target = targetIndex >= 0 ? CurrentTarget() : null;
        foreach (var e in enemies)
        {
            if (e.Dead) continue;
            Vector2 top = GuiPos(e.basePos + Vector3.up * e.size * 0.5f);
            Vector2 left = GuiPos(e.basePos + new Vector3(-e.size * 0.5f, e.size * 0.5f, 0f));
            Vector2 bar = GuiPos(W(e.basePos.x - transform.position.x, EnemyBarY - 0.12f * cellSize));
            float colW = Mathf.Abs(GuiPos(e.basePos + Vector3.right * e.size).x - left.x) + 40f;

            DrawText(new Rect(top.x - colW * 0.5f, top.y - small * 1.4f, colW, small * 1.4f),
                     e.data.name, small, ElementColor(e.data.element), TextAnchor.MiddleCenter);
            DrawText(new Rect(left.x, top.y, colW, small * 1.4f),
                     $"TURN {e.countdown}", small, new Color(1f, 0.4f, 0.4f), TextAnchor.MiddleLeft);
            DrawText(new Rect(bar.x - colW * 0.5f, bar.y, colW, small * 1.3f),
                     $"{e.hp:N0}", Mathf.RoundToInt(small * 0.9f), Color.white, TextAnchor.UpperCenter);

            if (e == target)
            {
                Vector2 mid = GuiPos(e.basePos);
                DrawText(new Rect(mid.x - 100f, mid.y - small, 200f, small * 2f), "[ TARGET ]", small,
                         new Color(1f, 0.9f, 0.3f), TextAnchor.MiddleCenter);
            }
        }

        // --- チームHP or 操作時間バー ---
        Vector2 hpL = GuiPos(W(-BoardHalfW + 0.1f * cellSize, HpBarY + HpBarH * 0.5f));
        Vector2 hpR = GuiPos(W(BoardHalfW - 0.1f * cellSize, HpBarY - HpBarH * 0.5f));
        Rect hpRect = Rect.MinMaxRect(hpL.x, hpL.y, hpR.x, hpR.y);

        if (state == State.Dragging && moveStarted)
        {
            float ratio = Mathf.Clamp01(moveTimer / moveTimeLimit);
            GUI.color = new Color(0f, 0f, 0f, 0.9f);
            GUI.DrawTexture(hpRect, Texture2D.whiteTexture);
            GUI.color = Color.Lerp(new Color(1f, 0.3f, 0.3f), new Color(0.3f, 1f, 0.5f), ratio);
            GUI.DrawTexture(new Rect(hpRect.x, hpRect.y, hpRect.width * ratio, hpRect.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
        else
        {
            DrawText(new Rect(hpRect.x, hpRect.y, hpRect.width - 6f, hpRect.height),
                     $"{teamHp:N0} / {teamMaxHp:N0}", Mathf.RoundToInt(hpRect.height * 0.85f),
                     new Color(0.6f, 1f, 0.5f), TextAnchor.MiddleRight);
        }

        // --- コンボ数（盤面の上の方に表示） ---
        if (comboShowTimer > 0f && comboCount > 0)
        {
            Vector2 c = GuiPos(W(0f, BoardTopY - 0.9f * cellSize));
            int size = Mathf.RoundToInt(baseFont * 1.8f);
            float mul = 1f + (comboCount - 1) * comboBonus;
            DrawText(new Rect(0, c.y - size, Screen.width, size * 1.4f), $"{comboCount} COMBO", size, Color.white, TextAnchor.MiddleCenter);
            DrawText(new Rect(0, c.y + size * 0.4f, Screen.width, size), $"ATK x{mul:0.00}", Mathf.RoundToInt(size * 0.5f),
                     new Color(1f, 0.9f, 0.4f), TextAnchor.MiddleCenter);
        }

        // --- ダメージ・回復の数字 ---
        foreach (var f in floats)
        {
            Vector2 p = GuiPos(f.world + Vector3.up * f.time * 0.6f * cellSize);
            float a = 1f - Mathf.Clamp01((f.time - 0.8f) / 0.4f);
            int size = Mathf.RoundToInt(baseFont * 1.1f * f.sizeMul);
            var col = f.color; col.a = a;
            DrawText(new Rect(p.x - 200f, p.y - size, 400f, size * 2f), f.text, size, col, TextAnchor.MiddleCenter);
        }

        // --- バナー ---
        if (bannerTimer > 0f && !string.IsNullOrEmpty(bannerText))
        {
            int size = Mathf.RoundToInt(baseFont * 2.2f);
            Vector2 e = GuiPos(W(0f, EnemyY));
            DrawText(new Rect(0, e.y - size, Screen.width, size * 2f), bannerText, size, new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleCenter);
        }
    }
}
