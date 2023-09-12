using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Opsive.ThirdPersonController;
using amQuests.tpcSupport;

namespace amQuests
{
    /// <summary>
    /// Спаунит EmeraldAI-актёров
    /// 
    /// Также способен РЕспаунить Opsive-актёров. Чтобы они не респаунились через CharacterRespawner, 
    /// у них надо Health.DeactivateOnDeaht --> true & CharacterRespawner.RespawnOnDisable --> false
    /// 
    /// Спаунер срабатывает при нарушении триггера, либо по получении TurnOn.
    /// Если активации спаунера пока надо избежать, следует ДЕАКТИВИРОВАТЬ его объект GameObject.SetActive(false)
    /// </summary>
    public class EmeraldSpawner : MonoBehaviour
    {
        public enum BattleKind { Defence, Attack, Reach, Escort }

        [Tooltip("Вариант для респауна Opsive-актёров")]
        public bool OpsiveVariation = false;
        [Tooltip("Для Opsive-варианта: перемещать ли актёров в точки респауна при их первой активации?")]
        public bool InitiallyMoveActors = false;
        [Tooltip("Активировать точки респауна при своей активации, и наоборот")]
        public bool ManageRespawnPoints = false;
        [Tooltip("Максимальное одновременное кол-во существующих актёров")]
        public int MaxActors = 7;
        [Tooltip("Не превышать заданное Макс. Кол-во при усилении сложности")]
        public bool DontExceedMaxActors = false;
        [Tooltip("Максимальное кол-во инстанциаций")]
        public int MaxInstantiations = 21;
        [Tooltip("Префабы актёров")]
        public GameObject[] Prefabs;
        [Tooltip("Точки спауна")]
        public Transform[] Points;
        [Tooltip("Waypoints для варианта Opsive")]
        public GameObject waypoints;
        [Tooltip("Альтернативный массив Waypoints для случайного назначения")]
        public GameObject[] Ways;
        [Tooltip("Не менять в актере Waypoints, если они уже назначены в самом актере")]
        public bool DontChangeWaypoints = false;
        [Tooltip("Префаб эффекта перед созданием актёра (должен быть самоликвидирующимся!)")]
        public GameObject InstantiationEffect;
        [Tooltip("Задержка создания актёра (для отработки эффекта)")]
        public float InstantiationDelay = 0.5f;
        [Tooltip("Один актёр на одну точку спауна (MaxActors при этом не используется)")]
        public bool OneActorOnePoint = false;
        [Tooltip("Период между спаунами")]
        public float Period = 5.0f;
        [Tooltip("Задержка перед активацией")]
        public float ActivationDelay = 2.0f;
        [Tooltip("Лейеры, на которые срабатывает триггер активации")]
        public LayerMask layerMask = 1 << 31;
        [Tooltip("Останавливать спаунинг при выходе <Игрока>")]
        public bool StopOnExit = true;
        [Tooltip("Длительность усиления музыки после начала атаки")]
        public float AfterAttackDelay = 10.0f;
        [Tooltip("Время, на которое спаунер остаётся неактивным после своей остановки")]
        public float RestorePeriod = 60.0f;
        [Tooltip("Минимальное время работы спаунера, если > 0")]
        public float MinSpawnTime = 0.0f;
        [Tooltip("Задержка включения спаунера")]
        public float StartSpawnerDelay = 0.0f;

        [Tooltip("Включать-выключать индикатор автоматически?")]
        public bool ManageIndicator = true;
        [Tooltip("Только включать индикатор автоматически?")]
        public bool ManageOnlyStart = false;
        [Tooltip("Battle kind")]
        public BattleKind battleKind;
        [Tooltip("Коэффициент для индикатора")]
        public float IndicatorCoef = 1.0f;

        [Tooltip("Событие ВМЕСТО включения спаунера (в режиме Новел)")]
        public UnityEvent OnNovelBattleStart;
        [Tooltip("Событие включения спаунера")]
        public UnityEvent OnSpawnerStart;
        [Tooltip("Событие выключения спаунера")]
        public UnityEvent OnSpawnerStop;

        // это событие можно использовать только тогда, когда StopOnExit = false !!!
        [Tooltip("Событие, когда убиты все наспауненные (актёры)")]
        public UnityEvent OnSpawnerStayEmpty;
        [Tooltip("Интервал проверки очистки спаунера от всех актёров")]
        public float CheckClearInterval = 3.0f;

        private int CurActors = 0;
        private int CurInstantiations = 0;
        private bool isActive = false;
        private bool canActivate = true;
        private Collider m_collider;
        private float startTime;

        private float GetMinSpawnTime()
        {
            return MinSpawnTime * GameController.BattleDuration;
        }

        private int GetMaxInstantiations()
        {
            int result = Mathf.RoundToInt(MaxInstantiations * GameController.BattleDuration);
            if (result <= 0)
                result = 1;
            return result;
        }

        private int GetMaxActors()
        {
            int result;
            // можно ли повышать Макс. Кол-во одновременных актёров?
            if (!GameController.ExceedMaxActors || DontExceedMaxActors)
                result = MaxActors;
            else
                result = MaxActors * (int)Mathf.Pow(2, GameController.Difficulty);// = Mathf.RoundToInt(MaxActors * Mathf.Clamp(GameController.BattleDuration, 1.0f, 2.0f));

            // Проверить, чтобы макс кол-во одновременных актеров не было больше кол-ва инстаниаций
            int maxInst = GetMaxInstantiations();
            if (result > maxInst)
                result = maxInst;

            // Проверить, чтобы макс кол-во одновременных актеров для Opsive было не больше кол-ва префабов
            if (OpsiveVariation && result > Prefabs.Length)
                result = Prefabs.Length;

            // Если число одновр актеров получилось больше числа инсталляций, увеличить число инсталляций
            //if (m_maxActors > m_maxInstantiations)
            //    m_maxInstantiations = m_maxActors;
            return result;
        }

        private void Awake()
        {
            DontExceedMaxActors = true;
        }

        private void Start()
        {
            if (OpsiveVariation && Prefabs.Length < MaxActors && Debug.isDebugBuild)
                Debug.LogError(name + ": Prefabs.Length < MaxActors !!!");
            m_collider = GetComponent<Collider>();

            GameController.ChangeDifficultyEvent += DifficultyChanged;
            DifficultyChanged(GameController.Difficulty);
        }

        private void DifficultyChanged(int _value)
        {
            ShowSpawnerProgress();
        }

        private void ShowSpawnerProgress()
        {
            if (GetMinSpawnTime() > 0.0f)
                SpawnerIndicator.SpawnerProgress((Time.time - startTime) / GetMinSpawnTime() * IndicatorCoef);
            else
                SpawnerIndicator.SpawnerProgress(CurInstantiations / GetMaxInstantiations() * IndicatorCoef);
        }
        // После каждой (ре)активации спаунер снова может работать
        void OnEnable()
        {
            canActivate = true;
            //InitializeValues();
        }
        private void OnDisable()
        {
            GameController.ChangeDifficultyEvent -= DifficultyChanged;
            StopSpawner();
        }

        // обработчики SendMessage from AC
        public void TurnOn() {
            canActivate = true;
            if (StartSpawnerDelay == 0.0f)
                StartSpawner();
            else
                Invoke("StartSpawner", StartSpawnerDelay);
        }

        public void TurnOff() { canActivate = false; StopSpawner(); }

        private Transform lastChoice = null;
        private float nextTime = 0.0f;
        // Update is called once per frame
        void Update()
        {
            if (isActive && Time.time > nextTime// && (!TimeGone() || CurInstantiations < MaxInstantiations)
                )
            {
                ShowSpawnerProgress();
                if (!OpsiveVariation)
                {   // спауним актёров Emerald AI
                    Transform tr = null;
                    if (!OneActorOnePoint)
                    {
                        if (CurActors < GetMaxActors())
                        {
                            // пропустить варианты, совпадающие с предыдущим вариантом
                            while ((tr = Points[Random.Range(0, Points.Length)]) == lastChoice) ;
                            lastChoice = tr;
                            nextTime = Time.time + Period * Random.Range(0.5f, 1.5f);
                            InstantiateEffect(tr);
                        }
                    }
                    else
                    {
                        if (CurActors < Points.Length)
                        {
                            // найти точку спауна, не имеющую чайлдов (т.е. актёр на ней погиб или никогда не спаунился)
                            for (int i = 0; i < Points.Length; i++)
                                if (Points[i].childCount == 0)
                                {
                                    nextTime = Time.time + Period * Random.Range(0.5f, 1.5f);
                                    InstantiateEffect(Points[i]);
                                    break;
                                }
                        }
                    }
                }
                else
                if (GetActiveActors() < GetMaxActors())
                {
                    GameObject temp = null;
                    // пропустить уже активные объекты
                    for (int iter = 0; iter < Prefabs.Length * 40; iter++) {
                        if ((temp = Prefabs[Random.Range(0, Prefabs.Length)]).activeSelf)
                        {
                            temp = null;
                            continue;
                        }
                        else
                            break;
                    }
                    nextTime = Time.time + Period * Random.Range(0.75f, 1.25f);
                    if (temp && !temp.activeSelf)
                        ReActivateActor(temp);
                }
            }
        }
        
        // используемо ТОЛЬКО для Opsive-варианта
        private int GetActiveActors() {
            int result = 0;
            for (int i = 0; i < Prefabs.Length; i++)
                if (Prefabs[i].activeSelf)
                    result++;
            return result;
        }
        
        // активировать (т.е. респаунить) актёра Opsive
        private void ReActivateActor(GameObject actor) {
            //if (Debug.isDebugBuild)
            //    Debug.Log(name + ".ReActivateActor " + actor);
            DM_Manager manager = actor.GetComponent<DM_Manager>();
            if (manager)
            {
                CurInstantiations++;
                // разобраться с вейпойнтами этого актёра
                GameObject _waypoints = null;
                if (Ways == null || Ways.Length == 0)
                    _waypoints = waypoints;
                else
                    _waypoints = Ways[Random.Range(0, Ways.Length)];


                if (_waypoints)
                {
                    if (!(manager.Waypoints && DontChangeWaypoints))
                        manager.Waypoints = _waypoints;
                    //if (manager.SetupWaypoints)
                    //    manager.SetWaypoints(manager.Waypoints);
                }
                if (manager.WasActivated) // если этот актёр неактивен, но был уже активирован - значит, он мёртв => его надо респаунить
                {
                    if (_waypoints && manager.SetupWaypoints)
                        manager.SetWaypoints(_waypoints);

                    CharacterRespawner characterRespawner = actor.GetComponent<CharacterRespawner>();
                    _respawner = characterRespawner;
                    //Invoke("Respawn_Actor", ActivationDelay);
                    Respawn_Actor();
                }
                else // этот актёр ещё никогда не спаунился
                {
                    RespawnPoint _point = null;
                    Vector3 _position;
                    if (InitiallyMoveActors && Points != null && Points.Length > 0)
                    // надо поместить актёра в одну из точек спаунера (если они указаны)
                    {
                        // найти подходящую точку респауна
                        List<RespawnPoint> respawns = new List<RespawnPoint>();
                        for (int i = 0; i < Points.Length; i++)
                        {
                            _point = Points[i].GetComponent<RespawnPoint>();
                            if (_point && _point.IsValid())
                                respawns.Add(_point);
                        }
                        _point = null;
                        if (respawns.Count > 0) // если найдены валидные респаун-точки, выбрать одну из них случайно
                        {
                            _point = respawns[Random.Range(0, respawns.Count)];
                            _position = _point.transform.position;
                        }
                        else
                            _position = Points[Random.Range(0, Points.Length)].position;

                        RigidbodyCharacterController rigidbodyCharacterController = manager.GetComponent<RigidbodyCharacterController>();
                        if (rigidbodyCharacterController) // если у актёра есть RigidbodyCharacterController, то перемещать с его помощью
                            rigidbodyCharacterController.SetPosition(_position);
                        else
                            actor.transform.position = _position;
                    }
                    manager.SetNpcMode(0); // предусмотреть автоматический переход в ИИ режим при активации
                    // Сбросить Позу, если есть
                    manager.gameObject.SendMessage("StopPoseImmediate", SendMessageOptions.DontRequireReceiver);
                    // если найдена RespawnPoint и она имеет свой собственный Путь, то назначить именно его
                    if (_point && _point.Waypoints)
                        manager.Waypoints = _point.Waypoints;
                    // активировать
                    actor.SetActive(true);
                    // и разрешить использовать оружие и включить боевой режим
                    /*
                    actor.SendMessage("SetCivilBehav", 0, SendMessageOptions.DontRequireReceiver);
                    actor.SendMessage("EnableNpcMode", 0, SendMessageOptions.DontRequireReceiver);*/
                }
            }
            else
            if (Debug.isDebugBuild)
                Debug.LogError(name + " can't reactivate actor " + actor + " so he hasn't DM_Manager component!");
            
            // отключить спаунер, если время работы вышло И это был последний по возможному кол-ву актёр
            if (TimeGone() && CurInstantiations >= GetMaxInstantiations())
                StopSpawner();
        }

        // время работы вышло
        private bool TimeGone()
        {
            return (GetMinSpawnTime() < 0.1f || Time.time - startTime > GetMinSpawnTime());
        }

        private CharacterRespawner _respawner;
        private void Respawn_Actor() {
            //_respawner.gameObject.SetActive(true);
            _respawner.Spawn();
        }

        private Transform m_parent;
        private void InstantiateEffect(Transform _parent) {
            m_parent = _parent;
            if (InstantiationEffect != null)
            {
                GameObject temp = Instantiate(InstantiationEffect, m_parent.position, m_parent.rotation, m_parent);
                temp.SetActive(true);
            }
            Invoke("InstantiateActor", InstantiationDelay);
        }

        private void InstantiateActor() {
            GameObject temp = Instantiate(Prefabs[Random.Range(0, Prefabs.Length)], m_parent.position, m_parent.rotation, m_parent);
            temp.SetActive(false);
            EmeraldManager emeraldManager = temp.GetComponent<EmeraldManager>();
            emeraldManager.SetSpawner(this);
            emeraldManager.SetInitPos(m_parent.position);
            emeraldManager.DelayedActivation(InstantiationDelay);
            CurActors++;
            CurInstantiations++;
            // отключить спаунер, если время работы вышло И это был последний по возможному кол-ву актёр
            if (TimeGone() && CurInstantiations >= GetMaxInstantiations())
                StopSpawner();
        }

        // проверить, очистился ли спаунер от всех наспауненных актёров
        private void CheckSpawnerIsEmpty()
        {
            if (Debug.isDebugBuild)
                Debug.Log("Spawner " + name + ".CheckSpawnerIsEmpty()");
            if (!OpsiveVariation)
            { 
                for (int i = 0; i < Points.Length; i++)
                    if (Points[i].childCount > 0)
                    {
                        Invoke("CheckSpawnerIsEmpty", CheckClearInterval);
                        return;
                    }
            }
            else
                for (int i = 0; i < Prefabs.Length; i++) 
                    if (Prefabs[i].activeSelf)
                    { 
                        Invoke("CheckSpawnerIsEmpty", CheckClearInterval);
                        return;
                    }
            CancelInvoke("CheckSpawnerIsEmpty");
            if (Debug.isDebugBuild)
                Debug.Log("Spawner " + name + ".OnSpawnerStayEmpty.Invoke()");
            OnSpawnerStayEmpty.Invoke();
            // и теперь-то уж точно можно ослабить музыку
            if (SceneMusic.instance)
                SceneMusic.instance.SetDramaticOffset(0);
            if (ManageIndicator)
                SpawnerIndicator.SpawnerStopped();

            // окончательно деактивировать точки спауна
            if (ManageRespawnPoints)
            {
                for (int i = 0; i < Points.Length; i++)
                    Points[i].SendMessage("DisablePoint", SendMessageOptions.DontRequireReceiver);
            }
        }

        // используется в TurnOn и OnTriggerEnter
        private void StartSpawner() {
            if (isActive || !canActivate) return;
            if (SceneMusic.instance)
                SceneMusic.instance.SetDramaticOffset(1);
            if (GameController.NovelMode)
            {
                OnNovelBattleStart.Invoke();
                canActivate = false; // чтобы снова не запустился (от триггера, например)
                return;
            }
            if (ManageRespawnPoints)
                ActivateSpawnPoints(true);
            //CurActors = 0;
            CancelInvoke();
            CurInstantiations = 0;
            isActive = true;
            startTime = Time.time;
            OnSpawnerStart.Invoke();
            if (ManageIndicator || ManageOnlyStart)
            {
                SpawnerIndicator.SpawnerStarted();
                SpawnerIndicator.SpawnerProgress(0);
                SpawnerIndicator.SetBattleKind((int)battleKind);
            }
            SceneController.currentScene.EnableHUDs(1);
            if (AC.KickStarter.player != null)
                AC.KickStarter.player.gameObject.SendMessage("SetCivilBehav", 0, SendMessageOptions.DontRequireReceiver);
            if (SceneController.currentScene.sceneCompanion != null)
                SceneController.currentScene.sceneCompanion.gameObject.SendMessage("EnableNPCMode", 0, SendMessageOptions.DontRequireReceiver);
        }

        // остановить спаунер
        private void StopSpawner() {
            if (Debug.isDebugBuild)
                Debug.Log("Spawner " + name + ".StopSpawner()");
            if (!isActive) return;
            isActive = false;
            canActivate = false;
            if (ManageRespawnPoints)
                ActivateSpawnPoints(false);
            OnSpawnerStop.Invoke();
            // Разрешить возможность повторного запуска спаунера только по истечении указанного периода!
            Invoke("RestoreSpawner", RestorePeriod);
            // проверка, живы ли кто-то из наспауненных врагов
            Invoke("CheckSpawnerIsEmpty", CheckClearInterval);
            
            // зажечь индикаторы точек спауна
            if (battleKind == BattleKind.Attack) {
                for (int i = 0; i < Points.Length; i++)
                    if (Points[i] != null && Points[i].gameObject != null)
                        Points[i].SendMessage("ShowNavigationElement", SendMessageOptions.DontRequireReceiver);
            }
            if (SceneMusic.instance)
                SceneMusic.instance.SetDramaticOffset(2);
        }

        private bool m_isDestroing = false;
        private void OnApplicationQuit()
        {
            m_isDestroing = true;
        }

        /// <summary>
        /// Если точка имеет скрипт RespawnPoint, то DeactivatePoint
        /// если нет, то просто её задизаблить (SetActive(false))
        /// </summary>
        /// <param name="_value"></param>
        private void ActivateSpawnPoints(bool _value) {
            if (!_value && m_isDestroing) return;
            for (int i = 0; i < Points.Length; i++)
                if (Points[i] && Points[i].gameObject)
                {
                    RespawnPoint _point = Points[i].GetComponent<RespawnPoint>();
                    if (_point)
                    {
                        if (!_value)
                            _point.DeactivatePoint();
                        else
                            _point.ReactivatePoint();
                    }
                    else
                        Points[i].gameObject.SetActive(_value);
                }
        }

        public void DestroyAllCreatures() { }

        // вернуть спаунеру возможность реактивироваться
        private void RestoreSpawner() { canActivate = true; }

        // вызывается уничтожающимся актёром самостоятельно!
        public void DecActorCount() {
            CurActors--;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (isActive) return;
            if (other.isTrigger || layerMask != (layerMask | (1 << other.gameObject.layer))) return;
            CapsuleCollider temp = null;
            if ((temp = other.gameObject.GetComponent<CapsuleCollider>()) == null) return;
            if (Debug.isDebugBuild)
                Debug.Log(name + ": actor entered into trigger: " + other.name + ". ReInitializeValues()");
            if (StartSpawnerDelay == 0.0f)
                StartSpawner();
            else
                Invoke("StartSpawner", StartSpawnerDelay);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!StopOnExit || other.isTrigger) return;
            if (layerMask != (layerMask | (1 << other.gameObject.layer))) return;
            CapsuleCollider temp = null;
            if ((temp = other.gameObject.GetComponent<CapsuleCollider>()) == null) return;
            if (StartSpawnerDelay > 0.0f)
            {
                if (IsInvoking(""))
                    CancelInvoke("StartSpawner");
            }
            StopSpawner();
        }

        private bool inAttack = false;
        internal void AttackStarted() {
            SceneMusic.instance.SetAlarm();
            /*CancelInvoke("AfterAttack");
            Invoke("AfterAttack", AfterAttackDelay);
            if (!inAttack && SceneMusic.instance)
            {
                inAttack = true;
                //SceneMusic.instance.IncDramaticOffset(1);
            }*/
        }

        private void AfterAttack() {
            inAttack = false;
            if (SceneMusic.instance)
                SceneMusic.instance.IncDramaticOffset(-1);
        }

        public void SetMaxInstallations(int _value)
        {
            MaxInstantiations = _value;
            DifficultyChanged(GameController.Difficulty);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Draw an editor-only visualization of the activity sectors.
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (Points != null)
            {
                Color pointsGizmoColor = isActive ? Color.magenta : Color.blue;
                pointsGizmoColor.a = 0.5f;
                Gizmos.color = pointsGizmoColor;
                for (int i = 0; i < Points.Length; i++)
                if (Points[i] != null)
                {
                    float _radius = 3.0f;
                    RespawnPoint _respawnPoint = Points[i].GetComponent<RespawnPoint>();
                    if (_respawnPoint)
                        _radius = _respawnPoint.Radius;
                    //Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawSphere(Points[i].transform.position, _radius);
                }
            }
            if ((m_collider = GetComponent<Collider>()) != null)
            {
                if (m_collider is CapsuleCollider)
                {
                    CapsuleCollider capsuleCollider = m_collider as CapsuleCollider;
                    Color colliderGizmoColor = isActive ? Color.red : Color.yellow;
                    colliderGizmoColor.a = 0.5f;
                    Gizmos.color = colliderGizmoColor;
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawSphere(capsuleCollider.center, capsuleCollider.radius);
                }
                else
                if (m_collider is BoxCollider)
                {
                    BoxCollider boxCollider = m_collider as BoxCollider;
                    Color colliderGizmoColor = isActive ? Color.red : Color.yellow;
                    colliderGizmoColor.a = 0.5f;
                    Gizmos.color = colliderGizmoColor;
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawCube(boxCollider.center, boxCollider.size);
                }
            }
        }
#endif

    }
}