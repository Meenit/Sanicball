﻿using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Sanicball.UI
{
    public class PauseMenu : MonoBehaviour
    {
        private const string pauseTag = "Pause";

        [SerializeField]
        private GameObject firstSelected = null;

        [SerializeField]
        private Button contextSensitiveButton;
        [SerializeField]
        private Text contextSensitiveButtonLabel;

        public static bool GamePaused { get { return GameObject.FindWithTag(pauseTag); } }

        private void Start()
        {
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(firstSelected);
            Time.timeScale = 0;
            AudioListener.pause = true;

            if (SceneManager.GetActiveScene().name == "Lobby")
            {
                contextSensitiveButtonLabel.text = "Change match settings";
                contextSensitiveButton.onClick.AddListener(MatchSettings);
            }
            else
            {
                contextSensitiveButtonLabel.text = "Return to lobby";
                contextSensitiveButton.onClick.AddListener(BackToLobby);
            }
        }

        public void Close()
        {
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            Time.timeScale = 1;
            AudioListener.pause = false;
        }

        public void MatchSettings()
        {
            LobbyReferences.Active.MatchSettingsPanel.Show();
            Close();
        }

        public void BackToLobby()
        {
            var matchManager = FindObjectOfType<MatchManager>();
            if (matchManager)
            {
                matchManager.GoToLobby();
            }
            else
            {
                Debug.LogError("Cannot return to lobby: no match manager found to handle the request. Something is broken!");
            }
        }

        public void QuitMatch()
        {
            var matchManager = FindObjectOfType<MatchManager>();
            if (matchManager)
            {
                matchManager.QuitMatch();
            }
            else
            {
                //Backup solution in case the match manager bugs out for whatever reason
                //Why would it ever bug out? I have no clue
                SceneManager.LoadScene("Menu");
            }
        }
    }
}