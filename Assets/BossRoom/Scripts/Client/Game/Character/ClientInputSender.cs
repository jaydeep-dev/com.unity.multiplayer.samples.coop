using MLAPI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BossRoom.Client
{
    /// <summary>
    /// Captures inputs for a character on a client and sends them to the server.
    /// </summary>
    [RequireComponent(typeof(NetworkCharacterState))]
    public class ClientInputSender : NetworkedBehaviour
    {
        private const float k_MouseInputRaycastDistance = 100f;

        private const float k_MoveSendRateSeconds = 0.5f;

        private float m_LastSentMove;

        // Cache raycast hit array so that we can use non alloc raycasts
        private readonly RaycastHit[] k_CachedHit = new RaycastHit[4];

        // This is basically a constant but layer masks cannot be created in the constructor, that's why it's assigned int Awake.
        private LayerMask k_GroundLayerMask;
        private LayerMask k_ActionLayerMask;

        private NetworkCharacterState m_NetworkCharacter;

        /// <summary>
        /// This describes how a skill was requested. Skills requested via mouse click will do raycasts to determine their target; skills requested
        /// in other matters will use the stateful target stored in NetworkCharacterState. 
        /// </summary>
        public enum SkillTriggerStyle
        {
            None,        //no skill was triggered.
            MouseClick,  //skill was triggered via mouse-click implying you should do a raycast from the mouse position to find a target.
            Keyboard,    //skill was triggered via a Keyboard press, implying target should be taken from the active target.
            KeyboardRelease, //represents a released key.
            UI,          //skill was triggered from the UI, and similar to Keyboard, target should be inferred from the active target. 
        }

        /// <summary>
        /// This struct essentially relays the call params of RequestAction to FixedUpdate. Recall that we may need to do raycasts
        /// as part of doing the action, and raycasts done outside of FixedUpdate can give inconsistent results (otherwise we would
        /// just expose PerformAction as a public method, and let it be called in whatever scoped it liked. 
        /// </summary>
        /// <remarks>
        /// Reference: https://answers.unity.com/questions/1141633/why-does-fixedupdate-work-when-update-doesnt.html
        /// </remarks>
        private struct ActionRequest
        {
            public SkillTriggerStyle TriggerStyle;
            public ActionType RequestedAction;
        }

        /// <summary>
        /// List of ActionRequests that have been received since the last FixedUpdate ran. This is a static array, to avoid allocs, and
        /// because we don't really want to let this list grow indefinitely.
        /// </summary>
        private readonly ActionRequest[] m_ActionRequests = new ActionRequest[5];

        /// <summary>
        /// Number of ActionRequests that have been queued since the last FixedUpdate. 
        /// </summary>
        private int m_ActionRequestCount;

        private BaseActionInput m_CurrentSkillInput = null;
        private bool m_MoveRequest = false;


        Camera m_MainCamera;

        public event Action<Vector3> OnClientClick;

        /// <summary>
        /// Convenience getter that returns our CharacterData
        /// </summary>
        CharacterClass CharacterData => GameDataSource.Instance.CharacterDataByType[m_NetworkCharacter.CharacterType];

        public override void NetworkStart()
        {
            // TODO Don't use NetworkedBehaviour for just NetworkStart [GOMPS-81]
            if (!IsClient || !IsOwner)
            {
                enabled = false;
                // dont need to do anything else if not the owner
                return;
            }

            k_GroundLayerMask = LayerMask.GetMask(new[] { "Ground" });
            k_ActionLayerMask = LayerMask.GetMask(new[] { "PCs", "NPCs", "Ground" });

            // find the hero action UI bar
            GameObject actionUIobj = GameObject.FindGameObjectWithTag("HeroActionBar");
            actionUIobj.GetComponent<Visual.HeroActionBar>().RegisterInputSender(this);

            // find the emote bar to track its buttons
            GameObject emoteUIobj = GameObject.FindGameObjectWithTag("HeroEmoteBar");
            emoteUIobj.GetComponent<Visual.HeroEmoteBar>().RegisterInputSender(this);
            // once connected to the emote bar, hide it
            emoteUIobj.SetActive(false);
        }

        void Awake()
        {
            m_NetworkCharacter = GetComponent<NetworkCharacterState>();
            m_MainCamera = Camera.main;
        }

        public void FinishSkill()
        {
            m_CurrentSkillInput = null;
        }

        void FixedUpdate()
        {
            //play all ActionRequests, in FIFO order. 
            for (int i = 0; i < m_ActionRequestCount; ++i)
            {
                if( m_CurrentSkillInput != null )
                {
                    //actions requested while input is active are discarded, except for "Release" requests, which go through. 
                    if (m_ActionRequests[i].TriggerStyle == SkillTriggerStyle.KeyboardRelease )
                    {
                        m_CurrentSkillInput.OnReleaseKey();
                    }
                }
                else
                {
                    var actionData = GameDataSource.Instance.ActionDataByType[m_ActionRequests[i].RequestedAction];
                    if (actionData.ActionInput != null)
                    {
                        var skillPlayer = Instantiate(actionData.ActionInput);
                        skillPlayer.Initiate(m_NetworkCharacter, actionData.ActionTypeEnum, FinishSkill);
                        m_CurrentSkillInput = skillPlayer;
                    }
                    else
                    {
                        PerformSkill(actionData.ActionTypeEnum, m_ActionRequests[i].TriggerStyle);
                    }
                }
            }

            m_ActionRequestCount = 0;


            if( m_MoveRequest )
            {
                m_MoveRequest = false;
                if ( (Time.time - m_LastSentMove) > k_MoveSendRateSeconds)
                {
                    m_LastSentMove = Time.time;
                    var ray = m_MainCamera.ScreenPointToRay(Input.mousePosition);
                    if (Physics.RaycastNonAlloc(ray, k_CachedHit, k_MouseInputRaycastDistance, k_GroundLayerMask) > 0)
                    {
                    // The MLAPI_INTERNAL channel is a reliable sequenced channel. Inputs should always arrive and be in order that's why this channel is used.
                    m_NetworkCharacter.SendCharacterInputServerRpc(k_CachedHit[0].point);
                        //Send our client only click request
                        OnClientClick?.Invoke(k_CachedHit[0].point);
                    }
                }
            }
        }

        /// <summary>
        /// Perform a skill in response to some input trigger. This is the common method to which all input-driven skill plays funnel. 
        /// </summary>
        /// <param name="actionType">The action you want to play. Note that "Skill1" may be overriden contextually depending on the target.</param>
        /// <param name="triggerStyle">What sort of input triggered this skill?</param>
        private void PerformSkill(ActionType actionType, SkillTriggerStyle triggerStyle)
        {
            int numHits = 0;
            if (triggerStyle == SkillTriggerStyle.MouseClick)
            {
                var ray = m_MainCamera.ScreenPointToRay(Input.mousePosition);
                numHits = Physics.RaycastNonAlloc(ray, k_CachedHit, k_MouseInputRaycastDistance, k_ActionLayerMask);
            }

            int networkedHitIndex = -1;
            for (int i = 0; i < numHits; i++)
            {
                if (k_CachedHit[i].transform.GetComponent<NetworkedObject>())
                {
                    networkedHitIndex = i;
                    break;
                }
            }

            Transform hitTransform = networkedHitIndex >= 0 ? k_CachedHit[networkedHitIndex].transform : null;
            if (GetActionRequestForTarget(hitTransform, actionType, triggerStyle, out ActionRequestData playerAction))
            {
                //Don't trigger our move logic for another 500ms. This protects us from moving  just because we clicked on them to target them.
                m_LastSentMove = Time.time;
                m_NetworkCharacter.RecvDoActionServerRPC(playerAction);
            }
            else
            {
                // clicked on nothing... perform a "miss" attack on the spot they clicked on
                var data = new ActionRequestData();
                PopulateSkillRequest(k_CachedHit[0].point, actionType, ref data);
                m_NetworkCharacter.RecvDoActionServerRPC(data);
            }
        }

        /// <summary>
        /// When you right-click on something you will want to do contextually different things. For example you might attack an enemy,
        /// but revive a friend. You might also decide to do nothing (e.g. right-clicking on a friend who hasn't FAINTED).
        /// </summary>
        /// <param name="hit">The Transform of the entity we clicked on, or null if none.</param>
        /// <param name="actionType">The Action to build for</param>
        /// <param name="triggerStyle">How did this skill play get triggered? Mouse, Keyboard, UI etc.</param>
        /// <param name="resultData">Out parameter that will be filled with the resulting action, if any.</param>
        /// <returns>true if we should play an action, false otherwise. </returns>
        private bool GetActionRequestForTarget(Transform hit, ActionType actionType, SkillTriggerStyle triggerStyle, out ActionRequestData resultData)
        {
            resultData = new ActionRequestData();

            var targetNetObj = hit != null ? hit.GetComponent<NetworkedObject>() : null;

            //if we can't get our target from the submitted hit transform, get it from our stateful target in our NetworkCharacterState. 
            if (!targetNetObj && actionType != ActionType.GeneralTarget)
            {
                ulong targetId = m_NetworkCharacter.TargetId.Value;
                if (ActionUtils.IsValidTarget(targetId))
                {
                    targetNetObj = MLAPI.Spawning.SpawnManager.SpawnedObjects[targetId];
                }
            }

            var targetNetState = targetNetObj != null ? targetNetObj.GetComponent<NetworkCharacterState>() : null;
            if (targetNetState == null)
            {
                //Not a Character. In the future this could represent interacting with some other interactable, but for
                //now, it implies we just do nothing.
                return false;
            }

            //Skill1 may be contextually overridden if it was generated from a mouse-click. 
            if (actionType == CharacterData.Skill1 && triggerStyle == SkillTriggerStyle.MouseClick)
            {
                if (!targetNetState.IsNpc && targetNetState.NetworkLifeState.Value == LifeState.Fainted)
                {
                    //right-clicked on a downed ally--change the skill play to Revive. 
                    actionType = ActionType.GeneralRevive;
                }
            }

            // record our target in case this action uses that info (non-targeted attacks will ignore this)
            resultData.ActionTypeEnum = actionType;
            resultData.TargetIds = new ulong[] { targetNetState.NetworkId };
            PopulateSkillRequest(targetNetState.transform.position, actionType, ref resultData);
            return true;
        }

        /// <summary>
        /// Populates the ActionRequestData with additional information. The TargetIds of the action should already be set before calling this.
        /// </summary>
        /// <param name="hitPoint">The point in world space where the click ray hit the target.</param>
        /// <param name="action">The action to perform (will be stamped on the resultData)</param>
        /// <param name="resultData">The ActionRequestData to be filled out with additional information.</param>
        private void PopulateSkillRequest(Vector3 hitPoint, ActionType action, ref ActionRequestData resultData)
        {
            resultData.ActionTypeEnum = action;
            var actionInfo = GameDataSource.Instance.ActionDataByType[action];

            //most skill types should implicitly close distance. The ones that don't are explicitly set to false in the following switch.
            resultData.ShouldClose = true;

            switch (actionInfo.Logic)
            {
                //for projectile logic, infer the direction from the click position.
                case ActionLogic.LaunchProjectile:
                    Vector3 offset = hitPoint - transform.position;
                    offset.y = 0;
                    resultData.Direction = offset.normalized;
                    resultData.ShouldClose = false; //why? Because you could be lining up a shot, hoping to hit other people between you and your target. Moving you would be quite invasive.
                    return;
                case ActionLogic.Target:
                    resultData.ShouldClose = false;
                    return;
                case ActionLogic.Emote:
                    resultData.CancelMovement = true;
                    return;
                case ActionLogic.RangedFXTargeted:
                    if (resultData.TargetIds == null) { resultData.Position = hitPoint; }
                    return;
            }
        }

        /// <summary>
        /// Request an action be performed. This will occur on the next FixedUpdate. 
        /// </summary>
        /// <param name="action">the action you'd like to perform. </param>
        /// <param name="triggerStyle">What input style triggered this action.</param>
        public void RequestAction(ActionType action, SkillTriggerStyle triggerStyle )
        {
            if( m_ActionRequestCount < m_ActionRequests.Length )
            {
                m_ActionRequests[m_ActionRequestCount].RequestedAction = action;
                m_ActionRequests[m_ActionRequestCount].TriggerStyle = triggerStyle;
                m_ActionRequestCount++;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                RequestAction(CharacterData.Skill2, SkillTriggerStyle.Keyboard);
            }
            else if (Input.GetKeyUp(KeyCode.Alpha1))
            {
                RequestAction(CharacterData.Skill2, SkillTriggerStyle.KeyboardRelease);
            }

            if (Input.GetKeyDown(KeyCode.Alpha4))
            {
                RequestAction(ActionType.Emote1, SkillTriggerStyle.Keyboard);
            }
            if (Input.GetKeyDown(KeyCode.Alpha5))
            {
                RequestAction(ActionType.Emote2, SkillTriggerStyle.Keyboard);
            }
            if (Input.GetKeyDown(KeyCode.Alpha6))
            {
                RequestAction(ActionType.Emote3, SkillTriggerStyle.Keyboard);
            }
            if (Input.GetKeyDown(KeyCode.Alpha7))
            {
                RequestAction(ActionType.Emote4, SkillTriggerStyle.Keyboard);
            }

            if ( !EventSystem.current.IsPointerOverGameObject())
            {
                //this is a simple way to determine if the mouse is over a UI element. If it is, we don't perform mouse input logic,
                //to model the button "blocking" mouse clicks from falling through and interacting with the world.

                if (Input.GetMouseButtonDown(1))
                {
                    RequestAction(CharacterData.Skill1, SkillTriggerStyle.MouseClick);
                }

                if (Input.GetMouseButtonDown(0))
                {
                    RequestAction(ActionType.GeneralTarget, SkillTriggerStyle.MouseClick);
                }
                else if (Input.GetMouseButton(0))
                {
                    m_MoveRequest = true;
                }
            }
        }
    }
}