// Safety property: the operator must not reduce the replica count unless a
// successful decommission command was observed first in the same run.
spec NoScaleDownBeforeDecommission
    observes eDecommissionSucceeded, eScaleDownRequested {

    var decommissionSeen: bool;

    start state Monitoring {
        on eDecommissionSucceeded do {
            decommissionSeen = true;
        }
        on eScaleDownRequested do {
            assert decommissionSeen,
                "Scale-down requested without a prior successful decommission";
            decommissionSeen = false;
        }
    }
}
