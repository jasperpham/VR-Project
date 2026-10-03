using System.Collections.Generic;
using UnityEngine;

public class MuseumVisitTracker : MonoBehaviour
{
    [SerializeField, Min(1)] private int totalExhibits = 10;
    [SerializeField] private TMPro.TMP_Text progressText;
    [SerializeField] private GameObject rewardPanel;

    private readonly HashSet<ExhibitVisit> visitedExhibits = new();

    private void Awake()
    {
        if (rewardPanel != null)
            rewardPanel.SetActive(false);

        UpdateProgress();
    }

    public void RegisterVisit(ExhibitVisit exhibit)
    {
        // Each exhibit can only count once.
        if (exhibit == null || !visitedExhibits.Add(exhibit))
            return;

        UpdateProgress();

        if (visitedExhibits.Count >= totalExhibits)
        {
            if (rewardPanel != null)
                rewardPanel.SetActive(true);

            Debug.Log("Museum tour complete!");
        }
    }

    private void UpdateProgress()
    {
        if (progressText != null)
        {
            progressText.text =
                $"Exhibits visited: {visitedExhibits.Count} / {totalExhibits}";
        }
    }
}