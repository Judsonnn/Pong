using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Globalization;

public class UdpServerPong : MonoBehaviour
{
    UdpClient server;
    IPEndPoint anyEP;
    Thread receiveThread;

    Dictionary<string, int> clientIds = new Dictionary<string, int>();
    Dictionary<int, IPEndPoint> clientEndpoints = new Dictionary<int, IPEndPoint>();
    int nextId = 1;

    readonly object stateLock = new object();
    float[] paddleY = new float[3]; // índice 1 e 2 (jogador 1 e 2)
    Vector2 ballPos = Vector2.zero;
    Vector2 ballVel = new Vector2(4f, 2f);
    int[] score = new int[3]; // índice 1 e 2

    // --- vitória e contagem ---
    bool gameOver = false;
    int winnerId = 0;
    float countdownLeft;

    [Header("Configuração do campo")]
    public float paddleX1 = -8f;
    public float paddleX2 = 8f;
    public float paddleHalfHeight = 1f;
    public float topLimit = 4.5f;
    public float bottomLimit = -4.5f;
    public float ballSpeed = 5f;
    public float tickRate = 0.02f; // ~50 atualizações por segundo

    [Header("Regras da partida")]
    public int winScore = 11;            // pontos para vencer
    public float countdownSeconds = 3f;  // contagem antes de cada saque

    void Start()
    {
        server = new UdpClient(5001);
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            server.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch { /* não crítico em outras plataformas (Mac/Linux não têm esse IOControl) */ }

        anyEP = new IPEndPoint(IPAddress.Any, 0);
        receiveThread = new Thread(ReceiveData);
        receiveThread.IsBackground = true;
        receiveThread.Start();

        ResetBall(1);
        countdownLeft = countdownSeconds;

        InvokeRepeating(nameof(GameTick), 1f, tickRate);
        Debug.Log("Servidor Pong iniciado na porta 5001");
    }

    void ReceiveData()
    {
        while (true)
        {
            byte[] data;
            try
            {
                data = server.Receive(ref anyEP);
            }
            catch (SocketException)
            {
                // cliente fechou/travou e o pacote anterior não teve resposta - ignora e segue
                continue;
            }
            catch (System.Exception)
            {
                // socket foi fechado (OnApplicationQuit) - encerra a thread
                break;
            }

            string msg = Encoding.UTF8.GetString(data);
            string key = anyEP.Address + ":" + anyEP.Port;

            lock (stateLock)
            {
                if (!clientIds.ContainsKey(key))
                {
                    if (nextId > 2)
                    {
                        continue; // já existem 2 jogadores, ignora novas conexões
                    }
                    int id = nextId++;
                    clientIds[key] = id;
                    clientEndpoints[id] = new IPEndPoint(anyEP.Address, anyEP.Port);

                    string assignMsg = "ASSIGN:" + id;
                    byte[] assignData = Encoding.UTF8.GetBytes(assignMsg);
                    server.Send(assignData, assignData.Length, anyEP);

                    Debug.Log("[Servidor] Novo cliente conectado: " + key + " -> ID " + id);
                }

                int cid = clientIds[key];
                // garante que o endpoint mais recente é usado (porta pode variar)
                clientEndpoints[cid] = new IPEndPoint(anyEP.Address, anyEP.Port);

                if (msg.StartsWith("PADDLE:"))
                {
                    float y = float.Parse(msg.Substring(7), CultureInfo.InvariantCulture);
                    y = Mathf.Clamp(y, bottomLimit + paddleHalfHeight, topLimit - paddleHalfHeight);
                    paddleY[cid] = y;
                }
                else if (msg == "RESTART" && gameOver)
                {
                    RestartMatch();
                }
            }
        }
    }

    void GameTick()
    {
        lock (stateLock)
        {
            if (clientEndpoints.Count < 2)
            {
                // esperando os dois jogadores: mantém a contagem "armada"
                countdownLeft = countdownSeconds;
            }
            else if (!gameOver)
            {
                if (countdownLeft > 0f)
                {
                    countdownLeft -= tickRate;
                }
                else
                {
                    SimulateBall(tickRate);
                }
            }
            Broadcast();
        }
    }

    void SimulateBall(float dt)
    {
        ballPos += ballVel * dt;

        // colisão com teto/chão
        if (ballPos.y > topLimit) { ballPos.y = topLimit; ballVel.y = -ballVel.y; }
        if (ballPos.y < bottomLimit) { ballPos.y = bottomLimit; ballVel.y = -ballVel.y; }

        // colisão com paddle 1 (esquerda)
        if (ballVel.x < 0 && ballPos.x <= paddleX1 + 0.3f && ballPos.x >= paddleX1 - 0.3f)
        {
            if (Mathf.Abs(ballPos.y - paddleY[1]) <= paddleHalfHeight)
            {
                ballVel.x = -ballVel.x;
                ballPos.x = paddleX1 + 0.3f;
            }
        }

        // colisão com paddle 2 (direita)
        if (ballVel.x > 0 && ballPos.x >= paddleX2 - 0.3f && ballPos.x <= paddleX2 + 0.3f)
        {
            if (Mathf.Abs(ballPos.y - paddleY[2]) <= paddleHalfHeight)
            {
                ballVel.x = -ballVel.x;
                ballPos.x = paddleX2 - 0.3f;
            }
        }

        // ponto para o jogador 2 (bola passou do paddle esquerdo)
        if (ballPos.x < paddleX1 - 1f)
        {
            OnPoint(2, 1);
        }
        // ponto para o jogador 1 (bola passou do paddle direito)
        else if (ballPos.x > paddleX2 + 1f)
        {
            OnPoint(1, -1);
        }
    }

    void OnPoint(int scorer, int nextDirection)
    {
        score[scorer]++;
        ResetBall(nextDirection);

        if (score[scorer] >= winScore)
        {
            gameOver = true;
            winnerId = scorer;
            ballVel = Vector2.zero; // bola fica parada no centro
            Debug.Log("[Servidor] Jogador " + scorer + " venceu!");
        }
        else
        {
            countdownLeft = countdownSeconds; // contagem antes do próximo saque
        }
    }

    void RestartMatch()
    {
        score[1] = 0;
        score[2] = 0;
        gameOver = false;
        winnerId = 0;
        ResetBall(1);
        countdownLeft = countdownSeconds;
        Debug.Log("[Servidor] Partida reiniciada");
    }

    void ResetBall(int direction)
    {
        ballPos = Vector2.zero;
        float randomY = Random.Range(-1.5f, 1.5f);
        ballVel = new Vector2(ballSpeed * direction, randomY);
    }

    void Broadcast()
    {
        // countdown: -1 = aguardando jogadores, 0 = jogo rolando, >0 = segundos restantes
        int countdown;
        if (clientEndpoints.Count < 2) countdown = -1;
        else if (!gameOver && countdownLeft > 0f) countdown = Mathf.CeilToInt(countdownLeft);
        else countdown = 0;

        string state = "STATE:" +
            paddleY[1].ToString("F2", CultureInfo.InvariantCulture) + ";" +
            paddleY[2].ToString("F2", CultureInfo.InvariantCulture) + ";" +
            ballPos.x.ToString("F2", CultureInfo.InvariantCulture) + ";" +
            ballPos.y.ToString("F2", CultureInfo.InvariantCulture) + ";" +
            score[1] + ";" + score[2] + ";" +
            countdown + ";" + (gameOver ? winnerId : 0);

        byte[] data = Encoding.UTF8.GetBytes(state);
        foreach (var kvp in clientEndpoints)
        {
            server.Send(data, data.Length, kvp.Value);
        }
    }

    void OnApplicationQuit()
    {
        receiveThread.Abort();
        server.Close();
    }
}