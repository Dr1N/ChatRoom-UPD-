using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatServer
{
    class Program
    {
        //Коллекции данных

        static List<string> users = new List<string>(100);
        static List<Message> messages = new List<Message>(1000);

        static ReaderWriterLockSlim locker = new ReaderWriterLockSlim();

        //Соединение

        static TcpListener tcpServer;
        static UdpClient udpSender;
        static IPAddress tcpIp;
        static int tcpPort;
        static IPAddress udpIp;
        static int udpPort;

        //Вспомогательные

        static int timeout = 20;
        static char separator = '|';

        static void Main(string[] args)
        {
            try
            {
                tcpIp = IPAddress.Parse(ConfigurationManager.AppSettings["listenedIP"]);
                tcpPort = Int32.Parse(ConfigurationManager.AppSettings["listenedPort"]);
                udpIp = IPAddress.Parse(ConfigurationManager.AppSettings["sendingUdpIP"]);
                udpPort = Int32.Parse(ConfigurationManager.AppSettings["sendingUdpPort"]);

                tcpServer = new TcpListener(tcpIp, tcpPort);
                tcpServer.Start();
                udpSender = new UdpClient();

                Console.WriteLine("Сервер запущен (Ожидает подключений...)\n");

                Thread udpUsersThread = new Thread(SendUsersUdp);
                udpUsersThread.IsBackground = true;
                udpUsersThread.Start();

                Thread udpMessagesThread = new Thread(SendMessagesUdp);
                udpMessagesThread.IsBackground = true;
                udpMessagesThread.Start();

                while (true)
                {
                    TcpClient client = tcpServer.AcceptTcpClient();

                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("\nСоединение: {0}\t\n", client.Client.RemoteEndPoint.ToString());
                    Console.ResetColor();

                    UserState state = new UserState(client, timeout);

                    Thread proc = new Thread(ClientProcessing);
                    proc.IsBackground = true;
                    proc.Start(state);

                    Thread watch = new Thread(UserWatche);
                    watch.IsBackground = true;
                    watch.Start(state);
                }
            }
            catch (SocketException sexc)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(sexc.Message);
                Console.ResetColor();
            }
            catch (ConfigurationErrorsException cexc)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(cexc.Message);
                Console.ResetColor();
            }
            catch (FormatException fexc)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(fexc.Message);
                Console.ResetColor();
            }
            catch (Exception exc)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(exc);
                Console.ResetColor();
            }
            finally
            {
                if (tcpServer != null) { tcpServer.Stop(); }
                if (udpSender != null) { udpSender.Close(); }
            }
        }

        #region Обработка запросов пользователя

        static void ClientProcessing(object obj)
        {
            UserState state = obj as UserState;
            if (state == null) { return; }

            TcpClient client = state.TcpClient;
            NetworkStream NS = client.GetStream();

            string user = null;
            string userAddress = client.Client.RemoteEndPoint.ToString();

            try
            {
                while (true)
                {
                    string request = GetRequest(NS);
                    state.Counter = timeout;
                    
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Запрос:\t{0}", request);
                    Console.ResetColor();

                    string[] reqArr = request.Split(separator);
                    switch (reqArr[0])
                    {
                        //Логин

                        case "LGN":
                            if (AddUser(reqArr[1], user, NS) == true)
                            {
                                user = reqArr[1];
                                state.StartWatch();
                            }
                            break;

                        //Логаут

                        case "LGT":
                            state.IsOnline = false;
                            DisconnectUser(reqArr[1], user, NS);
                            user = null;
                            break;

                        //Сообщение пользователя

                        case "SMS":
                            AddMessages(reqArr[1], user, NS);
                            break;

                        case "EMP":
                            SendResponceToClient("OK|", NS);
                            break;

                        default:
                            Console.WriteLine("Неверная команда");
                            SendResponceToClient("ERR|Неверная команда", NS);
                            break;
                    }
                }
            }
            catch (SocketException sexc)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Соединение разорвано: {0}", userAddress);
                Console.WriteLine(sexc.Message);
                Console.ResetColor();
            }
            catch (ApplicationException ae)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Соединение разорвано: {0}", userAddress);
                Console.WriteLine(ae.Message);
                Console.ResetColor();
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Соединение разорвано: {0}", userAddress);
                Console.WriteLine(e.Message);
                Console.ResetColor();
            }
            finally
            {
                state.IsOnline = false;
                if (user != null)
                {
                    locker.EnterReadLock();
                    bool isExists = users.Contains(user);
                    locker.ExitReadLock();

                    if (isExists)
                    {
                        locker.EnterWriteLock();
                        users.Remove(user);
                        locker.ExitWriteLock();
                    }
                }
                if (client != null)
                {
                    client.Close();
                }
            }
        }

        static void UserWatche(object obj)
        {
            UserState state = obj as UserState;
            if (state == null) { return; }

            TcpClient client = state.TcpClient;
            string userAddress = state.TcpClient.Client.RemoteEndPoint.ToString();

            Console.WriteLine("Наблюдатель за {0} начал работу", userAddress);

            while (state.IsOnline)
            {
                try
                {
                    state.Wait();
                    Thread.Sleep(1000);
                    state.DecrementCounter();
                    if (state.Counter == 0)
                    {
                        Console.WriteLine("Пользователь {0} отключён по таймауту", userAddress);
                        if (state.TcpClient != null) { state.TcpClient.Close(); }
                        break;
                    }
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e.Message);
                    Console.ResetColor();
                    break;
                }
            }

            Console.WriteLine("Наблюдатель за пользователем {0} завершил работу", userAddress);
        }

        #endregion

        #region Работа с сетью
        
        static int GetRequestLength(NetworkStream netStream)
        {
            byte[] buffer = new byte[4];
            int cnt = netStream.Read(buffer, 0, 4);
            if (cnt != 4) { throw new ApplicationException("Ошибка передачи. Получено менее 4 байт"); }
            int size = BitConverter.ToInt32(buffer, 0);
            if (size > 1024) { throw new ApplicationException("Ошибка передачи. Размер запроса превышает допустимый размер"); }
            
            return size;
        }

        static string GetRequest(NetworkStream netStream)
        {
            int size = GetRequestLength(netStream);
            MemoryStream MS = new MemoryStream(size);
            byte[] buffer = new byte[1024];
            int bytes = 0;
            do
            {
                int cnt = netStream.Read(buffer, 0, buffer.Length);
                if (cnt == 0) { throw new ApplicationException("Ошибка передачи данных. Получено 0 байт"); }
                bytes += cnt;
                MS.Write(buffer, 0, cnt);

            } while(size > bytes);

            return Encoding.UTF8.GetString(MS.GetBuffer(), 0, bytes);
        }

        static void SendResponceToClient(string responce, NetworkStream netStream)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Ответ:\t{0}", responce);
            Console.ResetColor();

            //Длина ответа

            BinaryWriter BW = new BinaryWriter(netStream);
            int size = Encoding.UTF8.GetByteCount(responce);
            BW.Write(size);

            //Ответ

            byte[] buffer = Encoding.UTF8.GetBytes(responce);
            netStream.Write(buffer, 0, buffer.Length);
        }

        #endregion

        #region Сообщения и Пользователи

        static bool AddUser(string userLogin, string currentUset, NetworkStream netStream)
        {
            string responce = null;
            try
            {
                if (currentUset != null)
                {
                    responce = "ERR" + separator + "Вы уже залогинены на сервере";
                    return false;
                }
                else if (userLogin == null ||
                         userLogin.Trim().Length < 3 || 10 < userLogin.Trim().Length ||
                         userLogin.Contains(separator))
                {
                    responce = "ERR" + separator + "Некорректный логин";
                    return false;
                }
                else
                {
                    locker.EnterReadLock();
                    int userCnt = users.Count;
                    locker.ExitReadLock();

                    if (userCnt > 100)
                    {
                        responce = "ERR" + separator + "Сервер перегружен";
                        return false;
                    }

                    locker.EnterReadLock();
                    bool isExists = users.Contains(userLogin);
                    locker.ExitReadLock();

                    if (isExists)
                    {
                        responce = "ERR" + separator + "Данный логин уже используется";
                        return false;
                    }
                }

                //Добавление пользователя в список

                locker.EnterWriteLock();
                users.Add(userLogin);
                locker.ExitWriteLock();

                responce = String.Format("OK" + separator + "{0} добавлен", userLogin);
                return true;
            }
            catch
            {
                responce = "ERR" + separator + "Не удалось добавить пользователя";
                return false;
            }
            finally
            {
                SendResponceToClient(responce, netStream);
            }
        }

        static void AddMessages(string message, string user, NetworkStream netStream)
        {
            string responce = null;
            try
            {
                string time = DateTime.Now.ToShortTimeString();
                Message msg = new Message(time, user, message);

                locker.EnterReadLock();
                int mCnt = messages.Count;
                locker.ExitReadLock();

                if (mCnt >= 1000)
                {
                    locker.EnterWriteLock();
                    messages.RemoveRange(0, 100);
                    locker.ExitWriteLock();
                }

                locker.EnterWriteLock();
                messages.Add(msg);
                locker.ExitWriteLock();
                responce = "OK" + separator + "Сообщение добавлено";
            }
            catch
            {
                responce = "ERR" + separator + "Не удалось добавить сообщение";
            }
            finally
            {
                SendResponceToClient(responce, netStream);
            }
        }

        static void DisconnectUser(string userLogin, string currentUser, NetworkStream netStream)
        {
            string responce = null;
            try
            {
                if (userLogin != currentUser)
                {
                    responce = "ERR" + separator + "Этот логин не принадлежит текущему пользователю";
                    return;
                }

                locker.EnterReadLock();
                bool isExists = users.Contains(userLogin);
                locker.ExitReadLock();

                if (!isExists)
                {
                    responce = "ERR" + separator + "Этого пользователя нет в списке пользователей";
                    return;
                }

                locker.EnterWriteLock();
                users.Remove(userLogin);
                locker.ExitWriteLock();

                responce = "OK" + separator + "Пользователь отсоединён";
            }
            catch
            {
                responce = "ERR" + separator + "Не удалось отсоединить пользователя";
            }
            finally
            {
                SendResponceToClient(responce, netStream);
            }
        }

        #endregion

        #region UDP рассылка

        static void SendUsersUdp()
        {
            string usersList = "";
            byte[] buffer;
            IPEndPoint ipep = new IPEndPoint(udpIp, udpPort);

            Console.WriteLine("UDP рассылка списка юзеров:\t{0}:{1}", ipep.Address, ipep.Port);

            while (true)
            {
                try
                {
                    Thread.Sleep(3000);
                    usersList = "UL" + separator + GetUsersList();
                    buffer = Encoding.UTF8.GetBytes(usersList);
                    lock (udpSender)
                    {
                        udpSender.Send(buffer, buffer.Length, ipep);
                    }
                }
                catch(Exception e) 
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Рассылка пользователей по UDP прекращена");
                    Console.WriteLine(e.Message);
                    Console.ResetColor();
                }
            }
        }

        static void SendMessagesUdp()
        {
            string messagesList = "";
            int lastMessages = 0;
            byte[] buffer;
            IPEndPoint ipep = new IPEndPoint(udpIp, udpPort);

            Console.WriteLine("UDP рассылка списка сообщений:\t{0}:{1}", ipep.Address, ipep.Port);

            while (true)
            {
                try
                {
                    Thread.Sleep(1500);
                    messagesList = "ML" + GetMessagesList(ref lastMessages);
                    buffer = Encoding.UTF8.GetBytes(messagesList);
                    lock (udpSender)
                    {
                        udpSender.Send(buffer, buffer.Length, ipep);
                    }
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Рассылка сообщений по UDP прекращена");
                    Console.WriteLine(e.Message);
                    Console.ResetColor();
                }
            }
        }

        static string GetUsersList()
        {
            try
            {
                string s = new string(separator, 1);
                locker.EnterReadLock();
                string[] usersArr = users.ToArray();
                return String.Join(s, usersArr);
            }
            finally
            {
                locker.ExitReadLock();
            }
        }

        static string GetMessagesList(ref int begin)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                locker.EnterReadLock();
                for (int i = begin; i < messages.Count; i++)
                {
                    sb.Append(messages[i].ToString());
                }
                begin = messages.Count;
                return sb.ToString();
            }
            finally
            {
                locker.ExitReadLock();
            }
        }

        #endregion
    }
}