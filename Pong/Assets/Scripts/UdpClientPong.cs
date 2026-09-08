using UnityEngine;
using TMPro;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Globalization;

public class UdpClientPong : MonoBehaviour
{
    UdpClient client;
    Thread receiveThread;
    IPEndPoint serverEP;

    int myId = -1;

    [Header("Referências de cena")]
    public GameObject localPaddle;
    public GameObject remotePaddle;
    public GameObject ball;
    public TextMeshProUGUI scoreText; // opcional - arraste um Text (TMP) da UI aqui

    [Header("Configuração")]
    public string serverIP = "127.0.0.1";
    public int serverPort = 5001;
    public float paddleSpeed = 6f;
    public float topLimit = 4.5f;
    public float bottomLimit = -4.5f;

    readonly object stateLock = new object();
    float remotePaddleY = 0f;
    Vector2 ballState = Vector2.zero;
    int score1 = 0, score2 = 0;
    bool hasState = false;

    void Start()
    {
        client = new UdpClient();
        serverEP = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);
        client.Connect(serverEP);

        receiveThread = new Thread(ReceiveData);
        receiveThread.IsBackground = true;
        receiveThread.Start();

        byte[] hello = Encoding.UTF8.GetBytes("HELLO");
        client.Send(hello, hello.Length);
    }

    void Update()
    {
        // movimento local do próprio paddle (só eixo vertical)
        float v = Input.GetAxis("Vertical");
        Vector3 pos = localPaddle.transform.position;
        pos.y = Mathf.Clamp(pos.y + v * paddleSpeed * Time.deltaTime, bottomLimit, topLimit);
        localPaddle.transform.position = pos;

        // envia a posição do paddle para o servidor
        string msg = "PADDLE:" + pos.y.ToString("F2", CultureInfo.InvariantCulture);
        byte[] data = Encoding.UTF8.GetBytes(msg);
        client.Send(data, data.Length);

        lock (stateLock)
        {
            if (hasState)
            {
                // paddle adversário e bola seguem o estado autoritativo do servidor
                Vector3 remotePos = remotePaddle.transform.position;
                remotePos.y = Mathf.Lerp(remotePos.y, remotePaddleY, Time.deltaTime * 15f);
                remotePaddle.transform.position = remotePos;

                Vector3 ballPos = ball.transform.position;
                Vector3 target = new Vector3(ballState.x, ballState.y, ballPos.z);
                ball.transform.position = Vector3.Lerp(ballPos, target, Time.deltaTime * 15f);

                if (scoreText != null)
                {
                    scoreText.text = score1 + "  x  " + score2;
                }
            }
        }
    }

    void ReceiveData()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            byte[] data = client.Receive(ref remoteEP);
            string msg = Encoding.UTF8.GetString(data);

            if (msg.StartsWith("ASSIGN:"))
            {
                myId = int.Parse(msg.Substring(7));
                Debug.Log("[Cliente] Meu ID = " + myId);
            }
            else if (msg.StartsWith("STATE:"))
            {
                string[] parts = msg.Substring(6).Split(';');
                if (parts.Length == 6)
                {
                    float p1 = float.Parse(parts[0], CultureInfo.InvariantCulture);
                    float p2 = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    float bx = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    float by = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    int s1 = int.Parse(parts[4]);
                    int s2 = int.Parse(parts[5]);

                    lock (stateLock)
                    {
                        // guarda o Y do paddle que NÃO é o nosso
                        remotePaddleY = (myId == 1) ? p2 : p1;
                        ballState = new Vector2(bx, by);
                        score1 = s1;
                        score2 = s2;
                        hasState = true;
                    }
                }
            }
        }
    }

    void OnApplicationQuit()
    {
        receiveThread.Abort();
        client.Close();
    }
}