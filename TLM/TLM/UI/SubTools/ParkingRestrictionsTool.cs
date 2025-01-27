﻿namespace TrafficManager.UI.SubTools {
    using System.Collections.Generic;
    using ColossalFramework;
    using Manager.Impl;
    using State;
    using Textures;
    using UnityEngine;
    using Util;
    using Util.Caching;
    using static Util.SegmentLaneTraverser;

    public class ParkingRestrictionsTool : SubTool {
        private readonly Dictionary<ushort, Dictionary<NetInfo.Direction, Vector3>> segmentCenterByDir
            = new Dictionary<ushort, Dictionary<NetInfo.Direction, Vector3>>();

        private const float SIGN_SIZE = 80f;

        /// <summary>
        /// Stores potentially visible segment ids while the camera did not move
        /// </summary>
        private GenericArrayCache<ushort> CachedVisibleSegmentIds { get; }

        /// <summary>
        /// Stores last cached camera position in <see cref="CachedVisibleSegmentIds"/>
        /// </summary>
        private CameraTransformValue LastCachedCamera { get; set; }

        public ParkingRestrictionsTool(TrafficManagerTool mainTool)
            : base(mainTool)
        {
            CachedVisibleSegmentIds = new GenericArrayCache<ushort>(NetManager.MAX_SEGMENT_COUNT);
            LastCachedCamera = new CameraTransformValue();
        }

        public override void OnActivate() { }

        public override void OnPrimaryClickOverlay() { }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo) { }

        public override void ShowGUIOverlay(ToolMode toolMode, bool viewOnly) {
            if (viewOnly && !Options.parkingRestrictionsOverlay) {
                return;
            }

            ShowSigns(viewOnly);
        }

        public override void Cleanup() {
            segmentCenterByDir.Clear();
            CachedVisibleSegmentIds.Clear();
            lastCamPos = null;
            lastCamRot = null;
        }

        private Quaternion? lastCamRot;
        private Vector3? lastCamPos;

        private void ShowSigns(bool viewOnly) {
            NetManager netManager = Singleton<NetManager>.instance;
            ParkingRestrictionsManager parkingManager = ParkingRestrictionsManager.Instance;

            var currentCamera = new CameraTransformValue(Camera.main);
            Transform currentCameraTransform = Camera.main.transform;
            Vector3 camPos = currentCameraTransform.position;

            if (!LastCachedCamera.Equals(currentCamera)) {
                // cache visible segments
                LastCachedCamera = currentCamera;
                CachedVisibleSegmentIds.Clear();

                const float MAX_DIST = TrafficManagerTool.MAX_OVERLAY_DISTANCE *
                                       TrafficManagerTool.MAX_OVERLAY_DISTANCE;

                for (uint segmentId = 1; segmentId < NetManager.MAX_SEGMENT_COUNT; ++segmentId) {
                    if (!Constants.ServiceFactory.NetService.IsSegmentValid((ushort)segmentId)) {
                        continue;
                    }

                    // if ((netManager.m_segments.m_buffer[segmentId].m_flags
                    //     & NetSegment.Flags.Untouchable) != NetSegment.Flags.None)
                    // continue;
                    Vector3 distToCamera = netManager.m_segments.m_buffer[segmentId].m_bounds.center - camPos;
                    if (distToCamera.sqrMagnitude > MAX_DIST) {
                        continue; // do not draw if too distant
                    }

                    bool visible = MainTool.WorldToScreenPoint(
                        netManager.m_segments.m_buffer[segmentId].m_bounds.center,
                        out Vector3 _);

                    if (!visible) {
                        continue;
                    }

                    if (!parkingManager.MayHaveParkingRestriction((ushort)segmentId)) {
                        continue;
                    }

                    CachedVisibleSegmentIds.Add((ushort)segmentId);
                } // end for all segments
            }

            bool clicked = !viewOnly && MainTool.CheckClicked();

            for (int segmentIdIndex = CachedVisibleSegmentIds.Size - 1;
                 segmentIdIndex >= 0;
                 segmentIdIndex--)
            {
                ushort segmentId = CachedVisibleSegmentIds.Values[segmentIdIndex];

                // draw parking restrictions
                if ((MainTool.GetToolMode() == ToolMode.SpeedLimits)
                    || ((MainTool.GetToolMode() == ToolMode.VehicleRestrictions)
                        && (segmentId == SelectedSegmentId)))
                {
                    continue;
                }

                // no parking restrictions overlay on selected segment when in vehicle restrictions mode
                DrawParkingRestrictionHandles(
                    segmentId,
                    clicked,
                    ref netManager.m_segments.m_buffer[segmentId],
                    viewOnly,
                    ref camPos);
            }
        }

        private bool DrawParkingRestrictionHandles(ushort segmentId,
                                                   bool clicked,
                                                   ref NetSegment segment,
                                                   bool viewOnly,
                                                   ref Vector3 camPos) {
            if (viewOnly && !Options.parkingRestrictionsOverlay) {
                return false;
            }

            NetManager netManager = Singleton<NetManager>.instance;
            ParkingRestrictionsManager parkingManager = ParkingRestrictionsManager.Instance;
            bool hovered = false;

            // draw parking restriction signs over mean middle points of lane beziers
            if (!segmentCenterByDir.TryGetValue(
                    segmentId,
                    out Dictionary<NetInfo.Direction, Vector3> segCenter))
            {
                segCenter = new Dictionary<NetInfo.Direction, Vector3>();
                segmentCenterByDir.Add(segmentId, segCenter);
                TrafficManagerTool.CalculateSegmentCenterByDir(segmentId, segCenter);
            }

            foreach (KeyValuePair<NetInfo.Direction, Vector3> e in segCenter) {
                bool allowed = parkingManager.IsParkingAllowed(segmentId, e.Key);
                if (allowed && viewOnly) {
                    continue;
                }

                bool visible = MainTool.WorldToScreenPoint(e.Value, out Vector3 screenPos);

                if (!visible) {
                    continue;
                }

                float zoom = (1.0f / (e.Value - camPos).magnitude) * 100f * MainTool.GetBaseZoom();
                float size = (viewOnly ? 0.8f : 1f) * SIGN_SIZE * zoom;
                Color guiColor = GUI.color;
                Rect boundingBox = new Rect(
                    screenPos.x - (size / 2),
                    screenPos.y - (size / 2),
                    size,
                    size);

                if (Options.speedLimitsOverlay) {
                    boundingBox.y -= size + 10f;
                }

                bool hoveredHandle = !viewOnly && TrafficManagerTool.IsMouseOver(boundingBox);

                guiColor.a = TrafficManagerTool.GetHandleAlpha(hoveredHandle);

                if (hoveredHandle) {
                    // mouse hovering over sign
                    hovered = true;
                }

                GUI.color = guiColor;
                GUI.DrawTexture(boundingBox, RoadUITextures.ParkingRestrictionTextures[allowed]);

                if (hoveredHandle && clicked && !IsCursorInPanel() &&
                    parkingManager.ToggleParkingAllowed(segmentId, e.Key)) {
                    allowed = !allowed;

                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    {
                        NetInfo.Direction normDir = e.Key;

                        if ((netManager.m_segments.m_buffer[segmentId].m_flags &
                             NetSegment.Flags.Invert) != NetSegment.Flags.None) {
                            normDir = NetInfo.InvertDirection(normDir);
                        }

                        bool LaneVisitor(SegmentLaneVisitData data) {
                            if (data.SegVisitData.Initial) {
                                return true;
                            }

                            bool reverse = data.SegVisitData.ViaStartNode ==
                                           data.SegVisitData.ViaInitialStartNode;

                            ushort otherSegmentId = data.SegVisitData.CurSeg.segmentId;
                            NetInfo otherSegmentInfo =
                                netManager.m_segments.m_buffer[otherSegmentId].Info;
                            byte laneIndex = data.CurLanePos.laneIndex;
                            NetInfo.Lane laneInfo = otherSegmentInfo.m_lanes[laneIndex];

                            NetInfo.Direction otherNormDir = laneInfo.m_finalDirection;
                            if (((netManager.m_segments.m_buffer[otherSegmentId].m_flags &
                                  NetSegment.Flags.Invert) != NetSegment.Flags.None) ^ reverse) {
                                otherNormDir = NetInfo.InvertDirection(otherNormDir);
                            }

                            if (otherNormDir == normDir) {
                                parkingManager.SetParkingAllowed(
                                    otherSegmentId,
                                    laneInfo.m_finalDirection,
                                    allowed);
                            }

                            return true;
                        }

                        SegmentLaneTraverser.Traverse(
                            segmentId,
                            SegmentTraverser.TraverseDirection.AnyDirection,
                            SegmentTraverser.TraverseSide.AnySide,
                            SegmentLaneTraverser.LaneStopCriterion.LaneCount,
                            SegmentTraverser.SegmentStopCriterion.Junction,
                            ParkingRestrictionsManager.LANE_TYPES,
                            ParkingRestrictionsManager.VEHICLE_TYPES,
                            LaneVisitor);
                    }
                }

                guiColor.a = 1f;
                GUI.color = guiColor;
            }

            return hovered;
        }
    }
}