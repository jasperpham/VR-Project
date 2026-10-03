using UnityEngine;

public class ExhibitVisit : MonoBehaviour
{
    [SerializeField] private MuseumVisitTracker tracker;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player"))
            return;

        if (tracker != null)
            tracker.RegisterVisit(this);
    }
}