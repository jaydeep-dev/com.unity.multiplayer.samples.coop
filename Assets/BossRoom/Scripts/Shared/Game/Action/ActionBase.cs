using UnityEngine;

namespace BossRoom
{
    /// <summary>
    /// Abstract base class containing some common members shared by Action (server) and ActionFX (client visual) 
    /// </summary>
    public abstract class ActionBase
    {
        protected ActionRequestData m_Data;

        /// <summary>
        /// Time when this Action was started (from Time.time) in seconds. Set by the ActionPlayer or ActionVisualization. 
        /// </summary>
        public float TimeStarted { get; set; }

        /// <summary>
        /// RequestData we were instantiated with. Value should be treated as readonly. 
        /// </summary>
        public ref ActionRequestData Data { get { return ref m_Data; } }

        /// <summary>
        /// Data Description for this action. 
        /// </summary>
        public ActionDescription Description
        {
            get
            {
                ActionDescription result;
                bool found = GameDataSource.Instance.ActionDataByType.TryGetValue(Data.ActionTypeEnum, out result);
                Debug.AssertFormat(found, "Tried to find ActionType %s but it was missing from GameDataSource!", Data.ActionTypeEnum);

                return GameDataSource.Instance.ActionDataByType[Data.ActionTypeEnum];
            }
        }

        public ActionBase(ref ActionRequestData data)
        {
            m_Data = data;
        }

    }

}