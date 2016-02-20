﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Discord;
using DiscordLogger;
using Discord.Legacy;
using System.IO;
using System.Collections.Specialized;
using System.Threading;

namespace DiscordLogger
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly int NUM_CHANNELS = 13;
        readonly int COUNTER_LOG_INTERVAL = 100; // log to counter file every # of msgs

        DiscordClient client;
        Dictionary<string, int> counters;
        string[] channels;

        string loginfile = "C:\\dlogs\\login.txt";  // lol username/password in plain text
        string filename = "C:\\dlogs\\log-";
        string fileExt = ".csv";
        string counterLog = "C:\\dlogs\\counter.txt";
        string statuslog = "C:\\dlogs\\status.txt";

        Role admin;
        Role founder;
        bool gotRoles = false;

        string username;
        string password;

        int[] sessionOffset;
        List<Label> totalCounterLabels;
        List<Label> sessionCounterLabels;

        DateTime programStartTime;
        DateTime sessionStartTime;
        DateTime now;
        DateTime nextDay;
        Timer dateTimer;

        public MainWindow()
        {
            InitializeComponent();
            client = new DiscordClient();
            counters = new Dictionary<string, int>();
            sessionOffset = new int[NUM_CHANNELS];
            sessionCounterLabels = new List<Label>();
            totalCounterLabels = new List<Label>();

            now = DateTime.UtcNow;
            nextDay = now.AddDays(1);   // used to reset session everyday
            dateTimer = new Timer(NewDay, null, nextDay - now, TimeSpan.FromHours(24));

            AddCounters();

            loggerCanvas.Visibility = Visibility.Collapsed;
            connectCanvas.Visibility = Visibility.Visible;

            buttonLogin.Click += ButtonLogin_Click;
            this.Closing += MainWindow_Closing;

            client.MessageReceived += Client_MessageReceived;
            client.GatewaySocket.Disconnected += GatewaySocket_Disconnected;
            client.GatewaySocket.Connected += GatewaySocket_Connected;

            // preload username/pw cause lazy
            if (File.Exists(loginfile))
            {
                string[] login = File.ReadAllLines(loginfile);
                textboxUsername.Text = login[0];
                textboxPassword.Password = login[1];
            }
        }



        /*
        *   Warning when user tries to close   
        */
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (MessageBox.Show("Do you want to exit?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                Application.Current.Shutdown();
            }
            else
            {
                e.Cancel = true;
            }
        }

        /*
        *   Login using the given email and password.
        *   Currently no error checking!
        */
        private void ButtonLogin_Click(object sender, RoutedEventArgs e)
        {
            username = textboxUsername.Text;
            password = textboxPassword.Password;

            ConnectToServer();

            loggerCanvas.Visibility = Visibility.Visible;
            connectCanvas.Visibility = Visibility.Collapsed;
        }

        /*
        *   Attempt to reconnect when disconnected.
        */
        private void GatewaySocket_Disconnected(object sender, DisconnectedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                statusLabel.Content = "Disconnected";
                string status = string.Format("Disconnected at {0:MM/dd HH:mm:ss}\n", now);
                errorLog.Content += status;
                File.AppendAllText(statuslog, status);

                while (client.State == ConnectionState.Disconnected)
                {
                    statusLabel.Content = "Disconnected";
                    ConnectToServer();  // try to reconnect
                    System.Threading.Thread.Sleep(5000);
                }
            });
        }

        /*
        *   Connected event
        */
        private void GatewaySocket_Connected(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (client.State == ConnectionState.Connected)
                {
                    statusLabel.Content = "Connected";
                    string status = string.Format("Connected at {0:MM/dd HH:mm:ss}\n", now);
                    errorLog.Content += status;
                    File.AppendAllText(statuslog, status);
                 }
            });
        }


        /*
        *   Connect to Discord, and create a new log file with current timestamp
        */
        async void ConnectToServer()
        {
            try
            {
                string x = await client.Connect(username, password);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    programStartTime = DateTime.UtcNow;
                    sessionStartTime = DateTime.UtcNow;
                    startTime.Content = string.Format("Program Started: {0:MM/dd HH:mm:ss}", programStartTime);
                    sessionTime.Content = string.Format("Session Started: {0:MM/dd HH:mm:ss}", sessionStartTime);
                });
            }
            catch (Exception e)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    errorLog.Content += ("connectError" + e.Message);
                });
            }
            LoadCounters();
        }

        /*
        *   Log a received message
        */
        void Client_MessageReceived(object sender, MessageEventArgs e)
        {
            Message msg = e.Message;

            // run once
            if (!gotRoles)
                GetRoles(e);

            // don't log own messages
            if (msg.IsAuthor)
                return;

            // status update TODO: expand to admins/founders
            //msg.User.Id == 105167204500123648
            if (msg.IsMentioningMe() && (msg.User.HasRole(admin) || msg.User.HasRole(founder)))
            {
                if (msg.Text.Contains("~status"))
                {
                    SendUpdateMsg(e.Channel);
                    File.WriteAllText(statuslog, string.Format("{0} called ~status at {1:MM/dd HH:mm:ss}", msg.User.Name, msg.Timestamp));
                }
            }
            LogMessage(msg);
        }


        /*
        *   Fetch the roles of admin/founder
        */
        private void GetRoles(MessageEventArgs e)
        {
            IEnumerable<Role> roles = e.Server.Roles;
            foreach (Role role in roles)
            {
                if (role.Name == "@Admin")
                    admin = role;
                if (role.Name == "@founder")
                    founder = role;
            }
            gotRoles = true;
        }


        /*
        *   Log a message
        */
        private void LogMessage(Message msg)
        {
            // parse message
            string channel = msg.Channel.Name;
            ulong cId = msg.Channel.Id;
            DateTime time = msg.Timestamp;
            string user = msg.User.Name;
            ulong uId = msg.User.Id;
            string text = msg.Text;

            //strip quotations
            text = text.Replace("\"", "$");
            text = text.Replace(",", "$");

            // check for attachments
            Message.Attachment[] att = msg.Attachments;
            char hasFile = 'n';
            if (att.Length > 0 ||
                text.Contains(".jpg") || text.Contains(".png") || text.Contains(".gif"))
            {
                hasFile = 'y';
            }

            // write line to file
            StringBuilder csv = new StringBuilder();
            string newLine = string.Format("\"{0:g}\",\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\"",
                                            time, channel, cId, user, uId, hasFile, text);
            csv.AppendLine(newLine);

            // increment counter
            counters["total"]++;
            if (counters.ContainsKey(channel))
                counters[channel]++;
            else
                counters["other"]++;
            UpdateCounters(newLine);

            try
            {
                string file = string.Format("{0}{1:yyyy-MM-dd}{2}", filename, now, fileExt);
                File.AppendAllText(file, csv.ToString());
            }
            catch (Exception err)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    errorLog.Content += ("writeError" + err.Message);
                });
            }
        }


        /*
        *   Update message counters
        */
        void UpdateCounters(string newLine)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (counters["total"] % 10 == 0)
                    currentMsg.Content = newLine;
                else
                    currentMsg.Content += "\n" + newLine;

                // log the total counter every 500 messages
                if (counters["total"] % COUNTER_LOG_INTERVAL == 0)
                    LogCounters();

                now = DateTime.UtcNow;
                timeNow.Content = string.Format("Last MSG: {0:MM/dd HH:mm:ss}", now);

                TimeSpan duration = now - programStartTime;
                elapsedTime.Content = string.Format("Time Elapsed: {0}days, {1:00}:{2:00}:{3:00}",
                                            duration.Days,
                                            duration.Hours,
                                            duration.Minutes,
                                            duration.Seconds);

                // update counter labels
                for (int i = 0; i < counters.Count; i++)
                {
                    totalCounterLabels[i].Content = counters[channels[i]];
                    sessionCounterLabels[i].Content = counters[channels[i]] - sessionOffset[i];
                }
            });
        }


        /*
        *   Send a status update when prompted by admin/founder
        */
        async private void SendUpdateMsg(Channel channel)
        {
            string status = "Total Messages\n";

            for (int i = 0; i < counters.Count; i++)
            {
                status += counters[channels[i]] + "   " + channels[i] + "\n";
            }

            await channel.SendMessage(status);

        }

        /*
        *   Log the counters to a file
        */
        void LogCounters()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> counter in counters)
            {
                sb.AppendLine(counter.Key +" "+counter.Value);
            }
            
            File.WriteAllText(counterLog, sb.ToString());
        }


        /*
        *   Callback for timer to reset session everyday
        */
        async void NewDay(Object obj)
        {

            string update = string.Format("Daily Update for {0:yyyy/MM/dd}\n", DateTime.UtcNow.AddDays(-1));

            for (int i = 0; i < counters.Count; i++)
            {
                update += (counters[channels[i]]-sessionOffset[i]) + "   " + channels[i] + "\n";
            }

            File.AppendAllText(statuslog, update);

            Channel botNetChan = client.GetChannel(145310829590478849);
            await botNetChan.SendMessage(update);

            ResetSession(null, null);
        }

        /*
        *   Reset session counter by saving the offset
        */
        private void ResetSession(object sender, RoutedEventArgs e)
        {
            // save offset
            for (int i = 0; i < counters.Count; i++)
            {
                sessionOffset[i] = counters[channels[i]];
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                sessionStartTime = DateTime.UtcNow;
                sessionTime.Content = string.Format("Session Started: {0:MM/dd HH:mm:ss}", sessionStartTime);
                
                // reset session counters on GUI
                foreach (Label sessionCounter in sessionCounterLabels)
                {
                    sessionCounter.Content = 0;
                }
            });
        }


        #region SETUP FUNCTIONS

        /*
        *   Initial setup
        */
        void AddCounters()
        {
            counters.Add("total", 0);
            counters.Add("general", 0);
            counters.Add("miscellaneous", 0);
            counters.Add("advice-serious", 0);
            counters.Add("meta", 0);
            counters.Add("anime-manga", 0);
            counters.Add("games", 0);
            counters.Add("kancolle", 0);
            counters.Add("idol-heaven", 0);
            counters.Add("fanart", 0);
            counters.Add("nsfw", 0);
            counters.Add("skynet", 0);
            counters.Add("other", 0);

            channels = counters.Keys.ToArray();

            // add totalLabels to list
            totalCounterLabels.Add(counter0);
            totalCounterLabels.Add(counter1);
            totalCounterLabels.Add(counter2);
            totalCounterLabels.Add(counter3);
            totalCounterLabels.Add(counter4);
            totalCounterLabels.Add(counter5);
            totalCounterLabels.Add(counter6);
            totalCounterLabels.Add(counter7);
            totalCounterLabels.Add(counter8);
            totalCounterLabels.Add(counter9);
            totalCounterLabels.Add(counter10);
            totalCounterLabels.Add(counter11);
            totalCounterLabels.Add(counter12);

            // add sessionLabels to list
            sessionCounterLabels.Add(sessionCounter0);
            sessionCounterLabels.Add(sessionCounter1);
            sessionCounterLabels.Add(sessionCounter2);
            sessionCounterLabels.Add(sessionCounter3);
            sessionCounterLabels.Add(sessionCounter4);
            sessionCounterLabels.Add(sessionCounter5);
            sessionCounterLabels.Add(sessionCounter6);
            sessionCounterLabels.Add(sessionCounter7);
            sessionCounterLabels.Add(sessionCounter8);
            sessionCounterLabels.Add(sessionCounter9);
            sessionCounterLabels.Add(sessionCounter10);
            sessionCounterLabels.Add(sessionCounter11);
            sessionCounterLabels.Add(sessionCounter12);
        }

        /*
        *   Load total counters from file
        */
        void LoadCounters()
        {
            if (File.Exists(counterLog))
            { 
                string[] counterData = File.ReadAllLines(counterLog);

                foreach (string countertext in counterData)
                {
                    // split on space, then read key value pair
                    string[] channel = countertext.Split(' ');
                    counters[channel[0]] = Int32.Parse(channel[1]);
                }

                // set session offsets properly
                ResetSession(null, null);
            }
        }
        #endregion
    }
}
