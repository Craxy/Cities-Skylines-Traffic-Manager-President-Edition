namespace TrafficManager.Manager.Impl {
    using System;
    using System.Collections.Generic;
    using API.Geometry;
    using API.Manager;
    using API.Traffic;
    using API.Traffic.Data;
    using ColossalFramework;
    using CSUtil.Commons;
    using Geometry;
    using JetBrains.Annotations;
    using State;
    using State.ConfigData;
    using Traffic;

    public class JunctionRestrictionsManager
        : AbstractGeometryObservingManager,
          ICustomDataManager<List<Configuration.SegmentNodeConf>>,
          IJunctionRestrictionsManager
    {
        public static JunctionRestrictionsManager Instance { get; } =
            new JunctionRestrictionsManager();

        private readonly SegmentFlags[] invalidSegmentFlags;

        /// <summary>
        /// Holds junction restrictions for each segment end
        /// </summary>
        private readonly SegmentFlags[] segmentFlags_;

        private JunctionRestrictionsManager() {
            segmentFlags_ = new SegmentFlags[NetManager.MAX_SEGMENT_COUNT];
            invalidSegmentFlags = new SegmentFlags[NetManager.MAX_SEGMENT_COUNT];
        }

        private void AddInvalidSegmentEndFlags(ushort segmentId,
                                               bool startNode,
                                               ref SegmentEndFlags endFlags) {
            if (startNode) {
                invalidSegmentFlags[segmentId].startNodeFlags = endFlags;
            } else {
                invalidSegmentFlags[segmentId].endNodeFlags = endFlags;
            }
        }

        protected override void HandleSegmentEndReplacement(SegmentEndReplacement replacement,
                                                            ref ExtSegmentEnd segEnd) {
            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;
            ISegmentEndId oldSegmentEndId = replacement.oldSegmentEndId;
            ISegmentEndId newSegmentEndId = replacement.newSegmentEndId;

            SegmentEndFlags flags;
            if (oldSegmentEndId.StartNode) {
                flags = invalidSegmentFlags[oldSegmentEndId.SegmentId].startNodeFlags;
                invalidSegmentFlags[oldSegmentEndId.SegmentId].startNodeFlags.Reset();
            } else {
                flags = invalidSegmentFlags[oldSegmentEndId.SegmentId].endNodeFlags;
                invalidSegmentFlags[oldSegmentEndId.SegmentId].endNodeFlags.Reset();
            }

            Services.NetService.ProcessNode(
                segEnd.nodeId,
                (ushort nId, ref NetNode node) => {
                    UpdateDefaults(
                        ref segEndMan.ExtSegmentEnds[segEndMan.GetIndex(
                                                         newSegmentEndId.SegmentId,
                                                         newSegmentEndId.StartNode)],
                        ref flags,
                        ref node);
                    return true;
                });

            Log._Debug(
                $"JunctionRestrictionsManager.HandleSegmentEndReplacement({replacement}): " +
                $"Segment replacement detected: {oldSegmentEndId.SegmentId} -> {newSegmentEndId.SegmentId} " +
                $"@ {newSegmentEndId.StartNode}");

            SetSegmentEndFlags(newSegmentEndId.SegmentId, newSegmentEndId.StartNode, flags);
        }

        public override void OnLevelLoading() {
            base.OnLevelLoading();
            for (uint i = 0; i < NetManager.MAX_SEGMENT_COUNT; ++i) {
                ExtSegment seg = Constants.ManagerFactory.ExtSegmentManager.ExtSegments[i];
                if (seg.valid) {
                    HandleValidSegment(ref seg);
                }
            }
        }

        protected override void InternalPrintDebugInfo() {
            base.InternalPrintDebugInfo();
            Log._Debug("Junction restrictions:");

            for (int i = 0; i < segmentFlags_.Length; ++i) {
                if (segmentFlags_[i].IsDefault()) {
                    continue;
                }

                Log._Debug($"Segment {i}: {segmentFlags_[i]}");
            }
        }

        private bool MayHaveJunctionRestrictions(ushort nodeId) {
            NetNode.Flags flags = NetNode.Flags.None;
            Services.NetService.ProcessNode(
                nodeId,
                (ushort nId, ref NetNode node) => {
                    flags = node.m_flags;
                    return true;
                });

            Log._Debug($"JunctionRestrictionsManager.MayHaveJunctionRestrictions({nodeId}): " +
                       $"flags={(NetNode.Flags)flags}");

            return LogicUtil.CheckFlags(
                       (uint)flags,
                       (uint)(NetNode.Flags.Created | NetNode.Flags.Deleted),
                       (uint)NetNode.Flags.Created)
                   && LogicUtil.CheckFlags(
                       (uint)flags,
                       (uint)(NetNode.Flags.Junction | NetNode.Flags.Bend));
        }

        public bool HasJunctionRestrictions(ushort nodeId) {
            if (!Services.NetService.IsNodeValid(nodeId)) {
                return false;
            }

            bool ret = false;
            Services.NetService.IterateNodeSegments(
                nodeId,
                (ushort segmentId, ref NetSegment segment) => {
                    if (segmentId == 0) {
                        return true;
                    }

                    bool startNode = segment.m_startNode == nodeId;
                    bool isDefault = startNode
                                         ? segmentFlags_[segmentId].startNodeFlags.IsDefault()
                                         : segmentFlags_[segmentId].endNodeFlags.IsDefault();

                    if (!isDefault) {
                        ret = true;
                        return false;
                    }

                    return true;
                });

            return ret;
        }

        private void RemoveJunctionRestrictions(ushort nodeId) {
            Log._Debug($"JunctionRestrictionsManager.RemoveJunctionRestrictions({nodeId}) called.");
            Services.NetService.IterateNodeSegments(
                nodeId,
                (ushort segmentId, ref NetSegment segment) => {
                    if (segmentId == 0) {
                        return true;
                    }

                    if (segment.m_startNode == nodeId) {
                        segmentFlags_[segmentId].startNodeFlags.Reset(false);
                    } else {
                        segmentFlags_[segmentId].endNodeFlags.Reset(false);
                    }

                    return true;
                });
        }

        [UsedImplicitly]
        public void RemoveJunctionRestrictionsIfNecessary() {
            for (uint nodeId = 0; nodeId < NetManager.MAX_NODE_COUNT; ++nodeId) {
                RemoveJunctionRestrictionsIfNecessary((ushort)nodeId);
            }
        }

        public void RemoveJunctionRestrictionsIfNecessary(ushort nodeId) {
            if (!MayHaveJunctionRestrictions(nodeId)) {
                RemoveJunctionRestrictions(nodeId);
            }
        }

        protected override void HandleInvalidSegment(ref ExtSegment seg) {
            foreach (bool startNode in Constants.ALL_BOOL) {
                SegmentEndFlags flags = startNode
                                            ? segmentFlags_[seg.segmentId].startNodeFlags
                                            : segmentFlags_[seg.segmentId].endNodeFlags;

                if (!flags.IsDefault()) {
                    AddInvalidSegmentEndFlags(seg.segmentId, startNode, ref flags);
                }

                segmentFlags_[seg.segmentId].Reset(startNode, true);
            }
        }

        protected override void HandleValidSegment(ref ExtSegment seg) {
            UpdateDefaults(ref seg);
        }

        public void UpdateAllDefaults() {
            IExtSegmentManager extSegmentManager = Constants.ManagerFactory.ExtSegmentManager;
            for (uint segmentId = 0; segmentId < NetManager.MAX_SEGMENT_COUNT; ++segmentId) {
                if (!Services.NetService.IsSegmentValid((ushort)segmentId)) {
                    continue;
                }

                UpdateDefaults(ref extSegmentManager.ExtSegments[segmentId]);
            }
        }

        private void UpdateDefaults(ref ExtSegment seg) {
            ushort segmentId = seg.segmentId;
            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;

            ushort startNodeId = Services.NetService.GetSegmentNodeId(seg.segmentId, true);
            Services.NetService.ProcessNode(
                startNodeId,
                (ushort nId, ref NetNode node) => {
                    UpdateDefaults(
                        ref segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, true)],
                        ref segmentFlags_[segmentId].startNodeFlags,
                        ref node);
                    return true;
                });

            ushort endNodeId = Services.NetService.GetSegmentNodeId(seg.segmentId, false);
            Services.NetService.ProcessNode(
                endNodeId,
                (ushort nId, ref NetNode node) => {
                    UpdateDefaults(
                        ref segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, false)],
                        ref segmentFlags_[segmentId].endNodeFlags,
                        ref node);
                    return true;
                });
        }

        private void UpdateDefaults(ref ExtSegmentEnd segEnd,
                                    ref SegmentEndFlags endFlags,
                                    ref NetNode node) {
            if (!IsUturnAllowedConfigurable(segEnd.segmentId, segEnd.startNode, ref node)) {
                endFlags.uturnAllowed = TernaryBool.Undefined;
            }

            if (!IsNearTurnOnRedAllowedConfigurable(segEnd.segmentId, segEnd.startNode, ref node)) {
                endFlags.nearTurnOnRedAllowed = TernaryBool.Undefined;
            }

            if (!IsFarTurnOnRedAllowedConfigurable(segEnd.segmentId, segEnd.startNode, ref node)) {
                endFlags.farTurnOnRedAllowed = TernaryBool.Undefined;
            }

            if (!IsLaneChangingAllowedWhenGoingStraightConfigurable(
                    segEnd.segmentId,
                    segEnd.startNode,
                    ref node)) {
                endFlags.straightLaneChangingAllowed = TernaryBool.Undefined;
            }

            if (!IsEnteringBlockedJunctionAllowedConfigurable(
                    segEnd.segmentId,
                    segEnd.startNode,
                    ref node)) {
                endFlags.enterWhenBlockedAllowed = TernaryBool.Undefined;
            }

            if (!IsPedestrianCrossingAllowedConfigurable(
                    segEnd.segmentId,
                    segEnd.startNode,
                    ref node)) {
                endFlags.pedestrianCrossingAllowed = TernaryBool.Undefined;
            }

            endFlags.defaultUturnAllowed = GetDefaultUturnAllowed(
                segEnd.segmentId,
                segEnd.startNode,
                ref node);
            endFlags.defaultNearTurnOnRedAllowed = GetDefaultNearTurnOnRedAllowed(
                segEnd.segmentId,
                segEnd.startNode,
                ref node);
            endFlags.defaultFarTurnOnRedAllowed = GetDefaultFarTurnOnRedAllowed(
                segEnd.segmentId,
                segEnd.startNode,
                ref node);
            endFlags.defaultStraightLaneChangingAllowed =
                GetDefaultLaneChangingAllowedWhenGoingStraight(
                    segEnd.segmentId,
                    segEnd.startNode,
                    ref node);
            endFlags.defaultEnterWhenBlockedAllowed = GetDefaultEnteringBlockedJunctionAllowed(
                segEnd.segmentId,
                segEnd.startNode,
                ref node);
            endFlags.defaultPedestrianCrossingAllowed = GetDefaultPedestrianCrossingAllowed(
                segEnd.segmentId,
                segEnd.startNode,
                ref node);

#if DEBUG
            if (DebugSwitch.JunctionRestrictions.Get()) {
                Log._DebugFormat(
                    "JunctionRestrictionsManager.UpdateDefaults({0}, {1}): Set defaults: " +
                    "defaultUturnAllowed={2}, defaultNearTurnOnRedAllowed={3}, " +
                    "defaultFarTurnOnRedAllowed={4}, defaultStraightLaneChangingAllowed={5}, " +
                    "defaultEnterWhenBlockedAllowed={6}, defaultPedestrianCrossingAllowed={7}",
                    segEnd.segmentId, segEnd.startNode, endFlags.defaultUturnAllowed,
                    endFlags.defaultNearTurnOnRedAllowed, endFlags.defaultFarTurnOnRedAllowed,
                    endFlags.defaultStraightLaneChangingAllowed,
                    endFlags.defaultEnterWhenBlockedAllowed,
                    endFlags.defaultPedestrianCrossingAllowed);
            }
#endif
        }

        public bool IsUturnAllowedConfigurable(ushort segmentId, bool startNode, ref NetNode node) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            bool ret =
                (node.m_flags & (NetNode.Flags.Junction | NetNode.Flags.Transition |
                                 NetNode.Flags.End | NetNode.Flags.Bend |
                                 NetNode.Flags.OneWayOut)) != NetNode.Flags.None
                && node.Info?.m_class?.m_service != ItemClass.Service.Beautification
                && !Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId].oneWay;
#if DEBUG
            if (DebugSwitch.JunctionRestrictions.Get()) {
                Log._DebugFormat(
                    "JunctionRestrictionsManager.IsUturnAllowedConfigurable({0}, {1}): ret={2}, " +
                    "flags={3}, service={4}, seg.oneWay={5}",
                    segmentId, startNode, ret, node.m_flags, node.Info?.m_class?.m_service,
                    Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId].oneWay);
            }
#endif
            return ret;
        }

        public bool GetDefaultUturnAllowed(ushort segmentId, bool startNode, ref NetNode node) {
#if DEBUG
            bool logLogic = DebugSwitch.JunctionRestrictions.Get();
#else
            const bool logLogic = false;
#endif

            if (!Constants.ManagerFactory.JunctionRestrictionsManager.IsUturnAllowedConfigurable(
                    segmentId,
                    startNode,
                    ref node)) {
                bool res = (node.m_flags & (NetNode.Flags.End | NetNode.Flags.OneWayOut)) !=
                           NetNode.Flags.None;
                if (logLogic) {
                    Log._Debug(
                        $"JunctionRestrictionsManager.GetDefaultUturnAllowed({segmentId}, " +
                        $"{startNode}): Setting is not configurable. res={res}, flags={node.m_flags}");
                }

                return res;
            }

            bool ret = (node.m_flags & (NetNode.Flags.End | NetNode.Flags.OneWayOut)) !=
                       NetNode.Flags.None;

            if (!ret && Options.allowUTurns) {
                ret = (node.m_flags & (NetNode.Flags.Junction | NetNode.Flags.Transition)) !=
                      NetNode.Flags.None;
            }

            if (logLogic) {
                Log._Debug(
                    $"JunctionRestrictionsManager.GetDefaultUturnAllowed({segmentId}, " +
                    $"{startNode}): Setting is configurable. ret={ret}, flags={node.m_flags}");
            }

            return ret;
        }

        public bool IsUturnAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].IsUturnAllowed(startNode);
        }

        public bool IsNearTurnOnRedAllowedConfigurable(ushort segmentId,
                                                       bool startNode,
                                                       ref NetNode node) {
            return IsTurnOnRedAllowedConfigurable(true, segmentId, startNode, ref node);
        }

        public bool IsFarTurnOnRedAllowedConfigurable(ushort segmentId,
                                                      bool startNode,
                                                      ref NetNode node) {
            return IsTurnOnRedAllowedConfigurable(false, segmentId, startNode, ref node);
        }

        public bool IsTurnOnRedAllowedConfigurable(bool near,
                                                   ushort segmentId,
                                                   bool startNode,
                                                   ref NetNode node) {
            ITurnOnRedManager turnOnRedMan = Constants.ManagerFactory.TurnOnRedManager;
            int index = turnOnRedMan.GetIndex(segmentId, startNode);
            bool lhd = Services.SimulationService.LeftHandDrive;
            bool ret =
                (node.m_flags & NetNode.Flags.TrafficLights) != NetNode.Flags.None &&
                (((lhd == near) && turnOnRedMan.TurnOnRedSegments[index].leftSegmentId != 0) ||
                 ((!lhd == near) && turnOnRedMan.TurnOnRedSegments[index].rightSegmentId != 0));
#if DEBUG
            if (DebugSwitch.JunctionRestrictions.Get()) {
                Log._Debug(
                    $"JunctionRestrictionsManager.IsTurnOnRedAllowedConfigurable({near}, " +
                    $"{segmentId}, {startNode}): ret={ret}");
            }
#endif

            return ret;
        }

        public bool GetDefaultNearTurnOnRedAllowed(ushort segmentId,
                                                   bool startNode,
                                                   ref NetNode node) {
            return GetDefaultTurnOnRedAllowed(true, segmentId, startNode, ref node);
        }

        public bool GetDefaultFarTurnOnRedAllowed(ushort segmentId,
                                                  bool startNode,
                                                  ref NetNode node) {
            return GetDefaultTurnOnRedAllowed(false, segmentId, startNode, ref node);
        }

        public bool GetDefaultTurnOnRedAllowed(bool near, ushort segmentId, bool startNode, ref NetNode node) {
#if DEBUG
            bool logLogic = DebugSwitch.JunctionRestrictions.Get();
#else
            const bool logLogic = false;
#endif

            if (!IsTurnOnRedAllowedConfigurable(near, segmentId, startNode, ref node)) {
                if (logLogic) {
                    Log._Debug(
                        $"JunctionRestrictionsManager.IsTurnOnRedAllowedConfigurable({near}, " +
                        $"{segmentId}, {startNode}): Setting is not configurable. res=false");
                }

                return false;
            }

            bool ret = near ? Options.allowNearTurnOnRed : Options.allowFarTurnOnRed;
            if (logLogic) {
                Log._Debug(
                    $"JunctionRestrictionsManager.GetTurnOnRedAllowed({near}, {segmentId}, " +
                    $"{startNode}): Setting is configurable. ret={ret}, flags={node.m_flags}");
            }

            return ret;
        }

        public bool IsTurnOnRedAllowed(bool near, ushort segmentId, bool startNode) {
            return near
                       ? IsNearTurnOnRedAllowed(segmentId, startNode)
                       : IsFarTurnOnRedAllowed(segmentId, startNode);
        }

        public bool IsNearTurnOnRedAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].IsNearTurnOnRedAllowed(startNode);
        }

        public bool IsFarTurnOnRedAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].IsFarTurnOnRedAllowed(startNode);
        }

        public bool IsLaneChangingAllowedWhenGoingStraightConfigurable(
            ushort segmentId,
            bool startNode,
            ref NetNode node) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            IExtSegmentManager segMan = Constants.ManagerFactory.ExtSegmentManager;
            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;

            bool isOneWay = segMan.ExtSegments[segmentId].oneWay;
            bool ret =
                (node.m_flags & (NetNode.Flags.Junction | NetNode.Flags.Transition)) !=
                NetNode.Flags.None &&
                node.Info?.m_class?.m_service != ItemClass.Service.Beautification &&
                !(isOneWay && segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, startNode)]
                                       .outgoing) && node.CountSegments() > 2;
#if DEBUG
            if (DebugSwitch.JunctionRestrictions.Get()) {
                Log._DebugFormat(
                    "JunctionRestrictionsManager.IsLaneChangingAllowedWhenGoingStraightConfigurable" +
                    "({0}, {1}): ret={2}, flags={3}, service={4}, outgoingOneWay={5}, " +
                    "node.CountSegments()={6}",
                    segmentId, startNode, ret, node.m_flags, node.Info?.m_class?.m_service,
                    isOneWay && segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, startNode)].outgoing,
                    node.CountSegments());
            }
#endif
            return ret;
        }

        public bool GetDefaultLaneChangingAllowedWhenGoingStraight(ushort segmentId, bool startNode, ref NetNode node) {
#if DEBUG
            bool logLogic = DebugSwitch.JunctionRestrictions.Get();
#else
            const bool logLogic = false;
#endif

            if (!Constants.ManagerFactory.JunctionRestrictionsManager
                          .IsLaneChangingAllowedWhenGoingStraightConfigurable(
                              segmentId,
                              startNode,
                              ref node)) {
                if (logLogic) {
                    Log._Debug(
                        "JunctionRestrictionsManager.GetDefaultLaneChangingAllowedWhenGoingStraight" +
                        $"({segmentId}, {startNode}): Setting is not configurable. res=false");
                }

                return false;
            }

            bool ret = Options.allowLaneChangesWhileGoingStraight;

            if (logLogic) {
                Log._Debug(
                    "JunctionRestrictionsManager.GetDefaultLaneChangingAllowedWhenGoingStraight" +
                    $"({segmentId}, {startNode}): Setting is configurable. ret={ret}");
            }

            return ret;
        }

        public bool IsLaneChangingAllowedWhenGoingStraight(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].IsLaneChangingAllowedWhenGoingStraight(startNode);
        }

        public bool IsEnteringBlockedJunctionAllowedConfigurable(
            ushort segmentId,
            bool startNode,
            ref NetNode node) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            IExtSegmentManager segMan = Constants.ManagerFactory.ExtSegmentManager;
            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;

            bool isOneWay = segMan.ExtSegments[segmentId].oneWay;
            bool ret = (node.m_flags & NetNode.Flags.Junction) != NetNode.Flags.None &&
                       node.Info?.m_class?.m_service != ItemClass.Service.Beautification &&
                       !(isOneWay
                         && segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, startNode)].outgoing);

#if DEBUG
            if (DebugSwitch.JunctionRestrictions.Get()) {
                Log._DebugFormat(
                    "JunctionRestrictionsManager.IsEnteringBlockedJunctionAllowedConfigurable" +
                    "({0}, {1}): ret={2}, flags={3}, service={4}, outgoingOneWay={5}",
                    segmentId, startNode, ret, node.m_flags, node.Info?.m_class?.m_service,
                    isOneWay && segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segmentId, startNode)].outgoing);
            }
#endif
            return ret;
        }

        public bool GetDefaultEnteringBlockedJunctionAllowed(
            ushort segmentId,
            bool startNode,
            ref NetNode node) {
#if DEBUG
            bool logLogic = DebugSwitch.JunctionRestrictions.Get();
#else
            const bool logLogic = false;
#endif
            if (! Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            if (!IsEnteringBlockedJunctionAllowedConfigurable(segmentId, startNode, ref node)) {
                bool res =
                    (node.m_flags & (NetNode.Flags.Junction | NetNode.Flags.OneWayOut |
                                     NetNode.Flags.OneWayIn)) != NetNode.Flags.Junction ||
                    node.CountSegments() == 2;
                if (logLogic) {
                    Log._DebugFormat(
                        "JunctionRestrictionsManager.GetDefaultEnteringBlockedJunctionAllowed" +
                        "({0}, {1}): Setting is not configurable. res={2}, flags={3}, " +
                        "node.CountSegments()={4}",
                        segmentId, startNode, res, node.m_flags, node.CountSegments());
                }

                return res;
            }

            bool ret;
            if (Options.allowEnterBlockedJunctions) {
                ret = true;
            } else {
                ushort nodeId = Services.NetService.GetSegmentNodeId(segmentId, startNode);
                int numOutgoing = 0;
                int numIncoming = 0;
                node.CountLanes(
                    nodeId,
                    0,
                    NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle,
                    VehicleInfo.VehicleType.Car,
                    true,
                    ref numOutgoing,
                    ref numIncoming);
                ret = numOutgoing == 1 || numIncoming == 1;
            }

            if (logLogic) {
                Log._Debug(
                    "JunctionRestrictionsManager.GetDefaultEnteringBlockedJunctionAllowed" +
                    $"({segmentId}, {startNode}): Setting is configurable. ret={ret}");
            }

            return ret;
        }

        public bool IsEnteringBlockedJunctionAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].IsEnteringBlockedJunctionAllowed(startNode);
        }

        public bool IsPedestrianCrossingAllowedConfigurable(ushort segmentId, bool startNode, ref NetNode node) {
            bool ret = (node.m_flags & (NetNode.Flags.Junction | NetNode.Flags.Bend)) != NetNode.Flags.None
                       && node.Info?.m_class?.m_service != ItemClass.Service.Beautification;
#if DEBUG
            if (DebugSwitch.JunctionRestrictions.Get()) {
                Log._Debug(
                    "JunctionRestrictionsManager.IsPedestrianCrossingAllowedConfigurable" +
                    $"({segmentId}, {startNode}): ret={ret}, flags={node.m_flags}, " +
                    $"service={node.Info?.m_class?.m_service}");
            }
#endif
            return ret;
        }

        public bool GetDefaultPedestrianCrossingAllowed(ushort segmentId, bool startNode, ref NetNode node) {
#if DEBUG
            bool logLogic = DebugSwitch.JunctionRestrictions.Get();
#else
            const bool logLogic = false;
#endif

            if (!IsPedestrianCrossingAllowedConfigurable(segmentId, startNode, ref node)) {
                if (logLogic) {
                    Log._Debug(
                        "JunctionRestrictionsManager.GetDefaultPedestrianCrossingAllowed" +
                        $"({segmentId}, {startNode}): Setting is not configurable. res=true");
                }

                return true;
            }

            // crossing is allowed at junctions and at untouchable nodes (for example: spiral
            // underground parking)
            bool ret = (node.m_flags & (NetNode.Flags.Junction | NetNode.Flags.Untouchable)) !=
                       NetNode.Flags.None;

            if (logLogic) {
                Log._Debug(
                    $"JunctionRestrictionsManager.GetDefaultPedestrianCrossingAllowed({segmentId}, " +
                    $"{startNode}): Setting is configurable. ret={ret}, flags={node.m_flags}");
            }

            return ret;
        }

        public bool IsPedestrianCrossingAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].IsPedestrianCrossingAllowed(startNode);
        }

        public TernaryBool GetUturnAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].GetUturnAllowed(startNode);
        }

        public TernaryBool GetNearTurnOnRedAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].GetNearTurnOnRedAllowed(startNode);
        }

        public TernaryBool GetFarTurnOnRedAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].GetFarTurnOnRedAllowed(startNode);
        }

        public TernaryBool GetTurnOnRedAllowed(bool near, ushort segmentId, bool startNode) {
            return near
                       ? GetNearTurnOnRedAllowed(segmentId, startNode)
                       : GetFarTurnOnRedAllowed(segmentId, startNode);
        }

        public TernaryBool GetLaneChangingAllowedWhenGoingStraight(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].GetLaneChangingAllowedWhenGoingStraight(startNode);
        }

        public TernaryBool GetEnteringBlockedJunctionAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].GetEnteringBlockedJunctionAllowed(startNode);
        }

        public TernaryBool GetPedestrianCrossingAllowed(ushort segmentId, bool startNode) {
            return segmentFlags_[segmentId].GetPedestrianCrossingAllowed(startNode);
        }

        public bool ToggleUturnAllowed(ushort segmentId, bool startNode) {
            return SetUturnAllowed(segmentId, startNode, !IsUturnAllowed(segmentId, startNode));
        }

        public bool ToggleNearTurnOnRedAllowed(ushort segmentId, bool startNode) {
            return ToggleTurnOnRedAllowed(true, segmentId, startNode);
        }

        public bool ToggleFarTurnOnRedAllowed(ushort segmentId, bool startNode) {
            return ToggleTurnOnRedAllowed(false, segmentId, startNode);
        }

        public bool ToggleTurnOnRedAllowed(bool near, ushort segmentId, bool startNode) {
            return SetTurnOnRedAllowed(near, segmentId, startNode, !IsTurnOnRedAllowed(near, segmentId, startNode));
        }

        public bool ToggleLaneChangingAllowedWhenGoingStraight(ushort segmentId, bool startNode) {
            return SetLaneChangingAllowedWhenGoingStraight(
                segmentId,
                startNode,
                !IsLaneChangingAllowedWhenGoingStraight(segmentId, startNode));
        }

        public bool ToggleEnteringBlockedJunctionAllowed(ushort segmentId, bool startNode) {
            return SetEnteringBlockedJunctionAllowed(
                segmentId,
                startNode,
                !IsEnteringBlockedJunctionAllowed(segmentId, startNode));
        }

        public bool TogglePedestrianCrossingAllowed(ushort segmentId, bool startNode) {
            return SetPedestrianCrossingAllowed(
                segmentId,
                startNode,
                !IsPedestrianCrossingAllowed(segmentId, startNode));
        }

        private void SetSegmentEndFlags(ushort segmentId, bool startNode, SegmentEndFlags flags) {
            if (flags.uturnAllowed != TernaryBool.Undefined) {
                SetUturnAllowed(segmentId, startNode, flags.IsUturnAllowed());
            }

            if (flags.nearTurnOnRedAllowed != TernaryBool.Undefined) {
                SetNearTurnOnRedAllowed(segmentId, startNode, flags.IsNearTurnOnRedAllowed());
            }

            if (flags.nearTurnOnRedAllowed != TernaryBool.Undefined) {
                SetFarTurnOnRedAllowed(segmentId, startNode, flags.IsFarTurnOnRedAllowed());
            }

            if (flags.straightLaneChangingAllowed != TernaryBool.Undefined) {
                SetLaneChangingAllowedWhenGoingStraight(
                    segmentId,
                    startNode,
                    flags.IsLaneChangingAllowedWhenGoingStraight());
            }

            if (flags.enterWhenBlockedAllowed != TernaryBool.Undefined) {
                SetEnteringBlockedJunctionAllowed(
                    segmentId,
                    startNode,
                    flags.IsEnteringBlockedJunctionAllowed());
            }

            if (flags.pedestrianCrossingAllowed != TernaryBool.Undefined) {
                SetPedestrianCrossingAllowed(
                    segmentId,
                    startNode,
                    flags.IsPedestrianCrossingAllowed());
            }
        }

        public bool SetUturnAllowed(ushort segmentId, bool startNode, bool value) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            if (!value && Constants.ManagerFactory.LaneConnectionManager.HasUturnConnections(
                    segmentId,
                    startNode)) {
                return false;
            }

            segmentFlags_[segmentId].SetUturnAllowed(startNode, value);
            OnSegmentChange(
                segmentId,
                startNode,
                ref Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId],
                true);
            return true;
        }

        public bool SetNearTurnOnRedAllowed(ushort segmentId, bool startNode, bool value) {
            return SetTurnOnRedAllowed(true, segmentId, startNode, value);
        }

        public bool SetFarTurnOnRedAllowed(ushort segmentId, bool startNode, bool value) {
            return SetTurnOnRedAllowed(false, segmentId, startNode, value);
        }

        public bool SetTurnOnRedAllowed(bool near, ushort segmentId, bool startNode, bool value) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            if (near) {
                segmentFlags_[segmentId].SetNearTurnOnRedAllowed(startNode, value);
            } else {
                segmentFlags_[segmentId].SetFarTurnOnRedAllowed(startNode, value);
            }
            OnSegmentChange(segmentId, startNode, ref Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId], true);
            return true;
        }

        public bool SetLaneChangingAllowedWhenGoingStraight(
            ushort segmentId,
            bool startNode,
            bool value) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            segmentFlags_[segmentId].SetLaneChangingAllowedWhenGoingStraight(startNode, value);
            OnSegmentChange(
                segmentId,
                startNode,
                ref Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId],
                true);
            return true;
        }

        public bool
            SetEnteringBlockedJunctionAllowed(ushort segmentId, bool startNode, bool value) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            segmentFlags_[segmentId].SetEnteringBlockedJunctionAllowed(startNode, value);

            // recalculation not needed here because this is a simulation-time feature
            OnSegmentChange(
                segmentId,
                startNode,
                ref Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId],
                false);
            return true;
        }

        public bool SetPedestrianCrossingAllowed(ushort segmentId, bool startNode, bool value) {
            if (!Services.NetService.IsSegmentValid(segmentId)) {
                return false;
            }

            segmentFlags_[segmentId].SetPedestrianCrossingAllowed(startNode, value);
            OnSegmentChange(
                segmentId,
                startNode,
                ref Constants.ManagerFactory.ExtSegmentManager.ExtSegments[segmentId],
                true);
            return true;
        }

        private void OnSegmentChange(ushort segmentId,
                                     bool startNode,
                                     ref ExtSegment seg,
                                     bool requireRecalc) {
            HandleValidSegment(ref seg);

            if (requireRecalc) {
                RoutingManager.Instance.RequestRecalculation(segmentId);
                if (OptionsManager.Instance.MayPublishSegmentChanges()) {
                    Services.NetService.PublishSegmentChanges(segmentId);
                }
            }
        }

        public override void OnLevelUnloading() {
            base.OnLevelUnloading();

            for (int i = 0; i < segmentFlags_.Length; ++i) {
                segmentFlags_[i].Reset(true);
            }

            for (int i = 0; i < invalidSegmentFlags.Length; ++i) {
                invalidSegmentFlags[i].Reset(true);
            }
        }

        public bool LoadData(List<Configuration.SegmentNodeConf> data) {
            bool success = true;
            Log.Info($"Loading junction restrictions. {data.Count} elements");

            foreach (Configuration.SegmentNodeConf segNodeConf in data) {
                try {
                    if (!Services.NetService.IsSegmentValid(segNodeConf.segmentId)) {
                        continue;
                    }

#if DEBUGLOAD
                    Log._Debug($"JunctionRestrictionsManager.LoadData: Loading junction restrictions for segment {segNodeConf.segmentId}: startNodeFlags={segNodeConf.startNodeFlags} endNodeFlags={segNodeConf.endNodeFlags}");
#endif

                    if (segNodeConf.startNodeFlags != null) {
                        ushort startNodeId =
                            Services.NetService.GetSegmentNodeId(segNodeConf.segmentId, true);
                        if (startNodeId != 0) {
                            Configuration.SegmentNodeFlags flags = segNodeConf.startNodeFlags;
                            Services.NetService.ProcessNode(
                                startNodeId,
                                delegate(ushort nId, ref NetNode node) {
                                    if (flags.uturnAllowed != null &&
                                        IsUturnAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            true,
                                            ref node)) {
                                        SetUturnAllowed(
                                            segNodeConf.segmentId,
                                            true,
                                            (bool)flags.uturnAllowed);
                                    }

                                    if (flags.turnOnRedAllowed != null &&
                                        IsNearTurnOnRedAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            true,
                                            ref node)) {
                                        SetNearTurnOnRedAllowed(
                                            segNodeConf.segmentId,
                                            true,
                                            (bool)flags.turnOnRedAllowed);
                                    }

                                    if (flags.farTurnOnRedAllowed != null &&
                                        IsFarTurnOnRedAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            true,
                                            ref node)) {
                                        SetFarTurnOnRedAllowed(
                                            segNodeConf.segmentId,
                                            true,
                                            (bool)flags.farTurnOnRedAllowed);
                                    }

                                    if (flags.straightLaneChangingAllowed != null &&
                                        IsLaneChangingAllowedWhenGoingStraightConfigurable(
                                            segNodeConf.segmentId,
                                            true,
                                            ref node)) {
                                        SetLaneChangingAllowedWhenGoingStraight(
                                            segNodeConf.segmentId,
                                            true,
                                            (bool)flags.straightLaneChangingAllowed);
                                    }

                                    if (flags.enterWhenBlockedAllowed != null &&
                                        IsEnteringBlockedJunctionAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            true,
                                            ref node)) {
                                        SetEnteringBlockedJunctionAllowed(
                                            segNodeConf.segmentId,
                                            true,
                                            (bool)flags.enterWhenBlockedAllowed);
                                    }

                                    if (flags.pedestrianCrossingAllowed != null &&
                                        IsPedestrianCrossingAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            true,
                                            ref node)) {
                                        SetPedestrianCrossingAllowed(
                                            segNodeConf.segmentId,
                                            true,
                                            (bool)flags.pedestrianCrossingAllowed);
                                    }

                                    return true;
                                });
                        } else {
                            Log.Warning(
                                "JunctionRestrictionsManager.LoadData(): Could not get segment " +
                                $"end geometry for segment {segNodeConf.segmentId} @ start node");
                        }
                    }

                    if (segNodeConf.endNodeFlags != null) {
                        ushort endNodeId =
                            Services.NetService.GetSegmentNodeId(segNodeConf.segmentId, false);
                        if (endNodeId != 0) {
                            Configuration.SegmentNodeFlags flags1 = segNodeConf.endNodeFlags;
                            Services.NetService.ProcessNode(
                                endNodeId,
                                delegate(ushort nId, ref NetNode node) {
                                    if (flags1.uturnAllowed != null &&
                                        IsUturnAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            false,
                                            ref node)) {
                                        SetUturnAllowed(
                                            segNodeConf.segmentId,
                                            false,
                                            (bool)flags1.uturnAllowed);
                                    }

                                    if (flags1.straightLaneChangingAllowed != null &&
                                        IsLaneChangingAllowedWhenGoingStraightConfigurable(
                                            segNodeConf.segmentId,
                                            false,
                                            ref node)) {
                                        SetLaneChangingAllowedWhenGoingStraight(
                                            segNodeConf.segmentId,
                                            false,
                                            (bool)flags1.straightLaneChangingAllowed);
                                    }

                                    if (flags1.enterWhenBlockedAllowed != null &&
                                        IsEnteringBlockedJunctionAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            false,
                                            ref node)) {
                                        SetEnteringBlockedJunctionAllowed(
                                            segNodeConf.segmentId,
                                            false,
                                            (bool)flags1.enterWhenBlockedAllowed);
                                    }

                                    if (flags1.pedestrianCrossingAllowed != null &&
                                        IsPedestrianCrossingAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            false,
                                            ref node)) {
                                        SetPedestrianCrossingAllowed(
                                            segNodeConf.segmentId,
                                            false,
                                            (bool)flags1.pedestrianCrossingAllowed);
                                    }

                                    if (flags1.turnOnRedAllowed != null &&
                                        IsNearTurnOnRedAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            false,
                                            ref node)) {
                                        SetNearTurnOnRedAllowed(
                                            segNodeConf.segmentId,
                                            false,
                                            (bool)flags1.turnOnRedAllowed);
                                    }

                                    if (flags1.farTurnOnRedAllowed != null &&
                                        IsFarTurnOnRedAllowedConfigurable(
                                            segNodeConf.segmentId,
                                            false,
                                            ref node)) {
                                        SetFarTurnOnRedAllowed(
                                            segNodeConf.segmentId,
                                            false,
                                            (bool)flags1.farTurnOnRedAllowed);
                                    }

                                    return true;
                                });
                        } else {
                            Log.Warning(
                                "JunctionRestrictionsManager.LoadData(): Could not get segment " +
                                $"end geometry for segment {segNodeConf.segmentId} @ end node");
                        }
                    }
                } catch (Exception e) {
                    // ignore, as it's probably corrupt save data. it'll be culled on next save
                    Log.Warning($"Error loading junction restrictions @ segment {segNodeConf.segmentId}: " + e);
                    success = false;
                }
            }

            return success;
        }

        public List<Configuration.SegmentNodeConf> SaveData(ref bool success) {
            var ret = new List<Configuration.SegmentNodeConf>();
            NetManager netManager = Singleton<NetManager>.instance;

            for (uint segmentId = 0; segmentId < NetManager.MAX_SEGMENT_COUNT; segmentId++) {
                try {
                    if (!Services.NetService.IsSegmentValid((ushort)segmentId)) {
                        continue;
                    }

                    Configuration.SegmentNodeFlags startNodeFlags = null;
                    Configuration.SegmentNodeFlags endNodeFlags = null;

                    ushort startNodeId = netManager.m_segments.m_buffer[segmentId].m_startNode;

                    if (Services.NetService.IsNodeValid(startNodeId)) {
                        SegmentEndFlags endFlags = segmentFlags_[segmentId].startNodeFlags;

                        if (!endFlags.IsDefault()) {
                            startNodeFlags = new Configuration.SegmentNodeFlags();

                            startNodeFlags.uturnAllowed =
                                TernaryBoolUtil.ToOptBool(GetUturnAllowed((ushort)segmentId, true));
                            startNodeFlags.turnOnRedAllowed = TernaryBoolUtil.ToOptBool(
                                GetNearTurnOnRedAllowed((ushort)segmentId, true));
                            startNodeFlags.farTurnOnRedAllowed = TernaryBoolUtil.ToOptBool(
                                GetFarTurnOnRedAllowed((ushort)segmentId, true));
                            startNodeFlags.straightLaneChangingAllowed = TernaryBoolUtil.ToOptBool(
                                GetLaneChangingAllowedWhenGoingStraight((ushort)segmentId, true));
                            startNodeFlags.enterWhenBlockedAllowed = TernaryBoolUtil.ToOptBool(
                                GetEnteringBlockedJunctionAllowed((ushort)segmentId, true));
                            startNodeFlags.pedestrianCrossingAllowed = TernaryBoolUtil.ToOptBool(
                                GetPedestrianCrossingAllowed((ushort)segmentId, true));

#if DEBUGSAVE
                            Log._Debug($"JunctionRestrictionsManager.SaveData: Saving start node "+
                            $"junction restrictions for segment {segmentId}: {startNodeFlags}");
#endif
                        }
                    }

                    ushort endNodeId = netManager.m_segments.m_buffer[segmentId].m_endNode;

                    if (Services.NetService.IsNodeValid(endNodeId)) {
                        SegmentEndFlags endFlags = segmentFlags_[segmentId].endNodeFlags;

                        if (!endFlags.IsDefault()) {
                            endNodeFlags = new Configuration.SegmentNodeFlags();

                            endNodeFlags.uturnAllowed =
                                TernaryBoolUtil.ToOptBool(
                                    GetUturnAllowed((ushort)segmentId, false));
                            endNodeFlags.turnOnRedAllowed = TernaryBoolUtil.ToOptBool(
                                GetNearTurnOnRedAllowed((ushort)segmentId, false));
                            endNodeFlags.farTurnOnRedAllowed = TernaryBoolUtil.ToOptBool(
                                GetFarTurnOnRedAllowed((ushort)segmentId, false));
                            endNodeFlags.straightLaneChangingAllowed = TernaryBoolUtil.ToOptBool(
                                GetLaneChangingAllowedWhenGoingStraight((ushort)segmentId, false));
                            endNodeFlags.enterWhenBlockedAllowed = TernaryBoolUtil.ToOptBool(
                                GetEnteringBlockedJunctionAllowed((ushort)segmentId, false));
                            endNodeFlags.pedestrianCrossingAllowed = TernaryBoolUtil.ToOptBool(
                                GetPedestrianCrossingAllowed((ushort)segmentId, false));

#if DEBUGSAVE
                            Log._Debug($"JunctionRestrictionsManager.SaveData: Saving end node junction "+
                            $"restrictions for segment {segmentId}: {endNodeFlags}");
#endif
                        }
                    }

                    if (startNodeFlags == null && endNodeFlags == null) {
                        continue;
                    }

                    var conf = new Configuration.SegmentNodeConf((ushort)segmentId);

                    conf.startNodeFlags = startNodeFlags;
                    conf.endNodeFlags = endNodeFlags;

#if DEBUGSAVE
                    Log._Debug($"Saving segment-at-node flags for seg. {segmentId}");
#endif
                    ret.Add(conf);
                } catch (Exception e) {
                    Log.Error(
                        $"Exception occurred while saving segment node flags @ {segmentId}: {e}");
                    success = false;
                }
            }

            return ret;
        }
    }
}