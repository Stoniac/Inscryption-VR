using System.Collections.Generic;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class MapFigurine
    {
        private const float PutBackSeconds = 0.2f;
        private const float MaxDropHeightSizes = 3f;

        private readonly ManualLogSource _logger;
        private VrHand _hand;
        private Carry _carry;
        private MapNode _hover;
        private float _size;
        private float _restY;

        internal MapFigurine(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal bool Holds(Component target)
        {
            return _carry != null && _carry.Holds(target);
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, bool input, float scale)
        {
            MapNodeManager manager = Singleton<MapNodeManager>.Instance;
            PlayerMarker marker = PlayerMarker.Instance;
            if (!input || !OnMap(manager, marker))
            {
                Release();
                return;
            }
            if (_carry != null)
            {
                Hold(manager, marker);
                return;
            }
            if (!TryGrab(grabber, right, marker, scale)) TryGrab(grabber, left, marker, scale);
            if (_carry == null)
            {
                Bounds bounds;
                Vector3 anchor = HandReach.RendererBounds(marker, out bounds) ? new Vector3(bounds.center.x, bounds.max.y, bounds.center.z) : marker.transform.position;
                VrTips.Show("map", anchor, "Pick up your figurine and set it down on a lit path.", 40);
            }
        }

        internal void Release()
        {
            SetHover(null);
            if (_carry != null) _carry.PutBack(PutBackSeconds);
            _carry = null;
            _hand = null;
        }

        private static bool OnMap(MapNodeManager manager, PlayerMarker marker)
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.Transitioning || flow.CurrentGameState != GameState.Map) return false;
            if (manager == null || manager.MovingNodes || marker == null || !marker.gameObject.activeInHierarchy) return false;
            if (marker.Anim == null || !marker.Anim.gameObject.activeInHierarchy) return false;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor != null && !cursor.InteractionDisabled;
        }

        private bool TryGrab(CardGrabber grabber, VrHand state, PlayerMarker marker, float scale)
        {
            if (!state.Ready || state.Busy) return false;
            float reach = grabber.ReachMeters * scale;
            bool touch = HandReach.RendererSqrDistance(marker, state.Position) <= reach * reach;
            if (touch) state.Highlight();
            if (state.GripEdge != 1) return false;
            if (!touch && !HandReach.PointsAtBounds(state, marker, scale)) return false;
            state.GripEdge = 0;
            state.Claimed = true;
            _hand = state;
            _size = Mathf.Max(HandReach.Size(marker), 0.02f * scale);
            _restY = marker.transform.position.y;
            _carry = new Carry(marker.transform, state, touch, state.HoldOffset(0.02f, scale), false);
            state.Play(Feel.Tap);
            state.SetHoldPose(HandHold.Wrap);
            _logger.LogInfo("Map figurine picked up with the " + state.Name + " hand (" + (touch ? "touch" : "point") + ").");
            return true;
        }

        private void Hold(MapNodeManager manager, PlayerMarker marker)
        {
            VrHand state = _hand;
            if (!state.Ready || !state.Grip.Held)
            {
                Drop(marker);
                return;
            }
            state.Claimed = true;
            state.SetHoldPose(HandHold.Wrap);
            _carry.Update();
            SetHover(FindNode(manager, marker.transform.position));
        }

        private MapNode FindNode(MapNodeManager manager, Vector3 position)
        {
            if (position.y - _restY > MaxDropHeightSizes * _size) return null;
            List<MapNode> nodes = manager.nodes;
            MapNode best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                MapNode node = nodes[i];
                if (!HandReach.Live(node)) continue;
                Bounds bounds = node.GetComponent<Collider>().bounds;
                Vector3 flat = new Vector3(position.x, bounds.center.y, position.z);
                float distance = bounds.SqrDistance(flat);
                if (distance <= _size * _size && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = node;
                }
            }
            return best;
        }

        private void SetHover(MapNode node)
        {
            if (node == _hover) return;
            if (_hover != null) _hover.CursorExit();
            _hover = node;
            if (_hover == null) return;
            _hover.CursorEnter();
            if (_hand != null) _hand.Play(Feel.Tick, 0.7f);
        }

        private void Drop(PlayerMarker marker)
        {
            MapNode node = _hover;
            VrHand state = _hand;
            _hover = null;
            _hand = null;
            Carry carry = _carry;
            _carry = null;
            if (node == null || !HandReach.Live(node))
            {
                if (node != null) node.CursorExit();
                carry.PutBack(PutBackSeconds);
                state.Play(Feel.Tick, 0.6f);
                return;
            }
            Vector3 position = marker.transform.position;
            marker.transform.position = new Vector3(position.x, _restY, position.z);
            node.CursorSelectStart();
            node.CursorSelectEnd();
            state.Play(Feel.Thud);
            VrTips.Done("map");
            _logger.LogInfo("Map figurine set down on node " + node.nodeId + "; travelling there.");
        }
    }
}
