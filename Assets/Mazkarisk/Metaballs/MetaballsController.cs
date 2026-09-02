using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class MetaballsController : MonoBehaviour {

	private const int MAX_SPHERE_COUNT = 65536; // 球の最大個数（シェーダー側と合わせる）

	[SerializeField]
	private Material material = null;

	[SerializeField]
	float smoothWidth = 0.1f;
	[SerializeField]
	float maxStakeholdableDistance = 0.2f;
	[SerializeField, Range(1, 256)]
	int maxMarchCount = 256;
	[SerializeField]
	float hitThreshold = 0.001f;
	[SerializeField]
	bool debugViewEnabled = false;

	private readonly Vector4[] _spheres = new Vector4[MAX_SPHERE_COUNT];
	ComputeBuffer spheresBuffer = null;

	// 物理演算用
	const float SPHERE_RADIUS = 0.02f;  // 球の表示上の半径
	const float SPHERE_FORCE_RADIUS = SPHERE_RADIUS * 4f;  // 球の引力・斥力等の影響半径
	const int GRID_DIVISION = 64; // グリッドの単一軸方向の分割数
	const float MAX_ACCELERATION_BY_PRESSURE = 100f; // 圧力シミュレート時の最大加速度

	int sphereCount = 8192;
	GameObject[] sphereObjects = new GameObject[MAX_SPHERE_COUNT];
	Rigidbody[] sphereRigidbodies = new Rigidbody[MAX_SPHERE_COUNT];

	List<int>[] sortedSpheres = new List<int>[GRID_DIVISION * GRID_DIVISION * GRID_DIVISION];
	Vector3[] spherePositions = new Vector3[MAX_SPHERE_COUNT];
	Vector3[] sphereVelocities = new Vector3[MAX_SPHERE_COUNT];
	Vector3[] sphereAccelerations = new Vector3[MAX_SPHERE_COUNT];

	void Start() {
		// メタボール(単体)のプレハブを読み込む。
		GameObject metaballPrefab = (GameObject)Resources.Load("Metaball");

		GetComponent<MeshFilter>().sharedMesh = CreateMeshForFullScreenEffect();
		GetComponent<MeshRenderer>().material = material;

		// メタボールの設定
		for (var i = 0; i < sphereCount; i++) {
			sphereObjects[i] = Instantiate(metaballPrefab, transform);
			Metaball metaballComponent = sphereObjects[i].GetComponent<Metaball>();
			metaballComponent.setAttributes(SPHERE_RADIUS, 1000f);
			sphereRigidbodies[i] = sphereObjects[i].GetComponent<Rigidbody>();
			RespawnBall(i);
		}

		int stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vector4));
		spheresBuffer = new ComputeBuffer(MAX_SPHERE_COUNT, stride, ComputeBufferType.Default);
	}

	void Update() {
		for (var i = 0; i < sphereCount; i++) {
			// 中心座標と半径を格納
			Vector3 position = sphereObjects[i].transform.position;
			_spheres[i] = new Vector4(position.x, position.y, position.z, SPHERE_RADIUS);
		}
		spheresBuffer.SetData(_spheres);

		material.SetInt("_SphereCount", sphereCount);
		material.SetFloat("_SmoothWidth", smoothWidth);
		material.SetFloat("_MaxStakeholdableDistance", maxStakeholdableDistance);
		material.SetFloat("_MaxMarchCount", maxMarchCount);
		material.SetFloat("_MaxMarchDistance", Camera.main.farClipPlane * 0.5f);
		material.SetFloat("_HitThreshold", hitThreshold);
		material.SetInt("_DebugViewEnabled", debugViewEnabled ? 1 : 0);
		material.SetBuffer("_SpheresBuffer", spheresBuffer);
	}

	void FixedUpdate() {
		System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
		logText = "";

		// リスポーン処理
		stopwatch.Restart();
		Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
		for (var i = 0; i < sphereCount; i++) {
			Vector3 position = sphereRigidbodies[i].position;

			// 一定以上落ちたボールはリスポーン
			if (position.y < -10) {
				RespawnBall(i);
				position = sphereRigidbodies[i].position;
			}

			min = Vector3.Min(min, position);
			max = Vector3.Max(max, position);

			spherePositions[i] = position;
			sphereVelocities[i] = sphereRigidbodies[i].linearVelocity;
			sphereAccelerations[i] = Vector3.zero;
		}
		stopwatch.Stop();
		logText += "リスポーン処理の時間 : " + stopwatch.Elapsed.TotalMilliseconds + " ms\n";

		// グリッドの中心と大きさを算出
		Vector3 gridCenter = (min + max) * 0.5f;
		Vector3 gridSize = Vector3.Max(max - min, Vector3.one * (SPHERE_FORCE_RADIUS * GRID_DIVISION)); // 一つ隣のグリッドまで見れば済むようにサイズを調整
		min = gridCenter - gridSize * 0.5f;
		max = gridCenter + gridSize * 0.5f;
		Vector3 gridCellSize = gridSize / GRID_DIVISION;

		// グリッド初期化
		stopwatch.Restart();
		Parallel.For(0, sortedSpheres.Length, i => {
			if (sortedSpheres[i] == null) {
				sortedSpheres[i] = new List<int>();
			}
			sortedSpheres[i].Clear();
		});
		stopwatch.Stop();
		logText += "グリッド初期化処理の時間 : " + stopwatch.Elapsed.TotalMilliseconds + " ms\n";

		// グリッド格納処理
		stopwatch.Restart();
		Parallel.For(0, sphereCount, i => {
			// グリッド内セルIDの特定
			Vector3 positionInGrid = spherePositions[i] - min;
			int x = Mathf.Clamp((int)(positionInGrid.x / gridCellSize.x), 0, GRID_DIVISION - 1);
			int y = Mathf.Clamp((int)(positionInGrid.y / gridCellSize.y), 0, GRID_DIVISION - 1);
			int z = Mathf.Clamp((int)(positionInGrid.z / gridCellSize.z), 0, GRID_DIVISION - 1);
			int cellIndex = GetCellIndex(x, y, z);

			// セルに追加
			sortedSpheres[cellIndex].Add(i);
		});
		stopwatch.Stop();
		logText += "グリッド格納処理の時間 : " + stopwatch.Elapsed.TotalMilliseconds + " ms\n";

		stopwatch.Restart();
		int maxLoopCount = 0;
		int actualPairCount = 0;
		Parallel.For(0, sphereCount, i => {
			// グリッド内セルIDの特定
			Vector3 positionInGrid = spherePositions[i] - min;
			int x = Mathf.Clamp((int)(positionInGrid.x / gridCellSize.x), 0, GRID_DIVISION - 1);
			int y = Mathf.Clamp((int)(positionInGrid.y / gridCellSize.y), 0, GRID_DIVISION - 1);
			int z = Mathf.Clamp((int)(positionInGrid.z / gridCellSize.z), 0, GRID_DIVISION - 1);

			// 対象となるセルの範囲
			int xStart = Mathf.Clamp(x - 1, 0, GRID_DIVISION - 1);
			int yStart = Mathf.Clamp(y - 1, 0, GRID_DIVISION - 1);
			int zStart = Mathf.Clamp(z - 1, 0, GRID_DIVISION - 1);
			int xEnd = Mathf.Clamp(x + 1, 0, GRID_DIVISION - 1);
			int yEnd = Mathf.Clamp(y + 1, 0, GRID_DIVISION - 1);
			int zEnd = Mathf.Clamp(z + 1, 0, GRID_DIVISION - 1);

			// セルの境界から十分に離れている場合は、その方向のセルは判定対象外とする。
			Vector3 positionInCell = new Vector3(
				Mathf.Repeat(positionInGrid.x, gridCellSize.x),
				Mathf.Repeat(positionInGrid.y, gridCellSize.y),
				Mathf.Repeat(positionInGrid.z, gridCellSize.z));

			if (positionInCell.x > SPHERE_FORCE_RADIUS) {
				xStart = x;
			}
			if (positionInCell.y > SPHERE_FORCE_RADIUS) {
				yStart = y;
			}
			if (positionInCell.z > SPHERE_FORCE_RADIUS) {
				zStart = z;
			}
			if (positionInCell.x < gridCellSize.x - SPHERE_FORCE_RADIUS) {
				xEnd = x;
			}
			if (positionInCell.y < gridCellSize.y - SPHERE_FORCE_RADIUS) {
				yEnd = y;
			}
			if (positionInCell.z < gridCellSize.z - SPHERE_FORCE_RADIUS) {
				zEnd = z;
			}

			for (int zIndex = zStart; zIndex <= zEnd; zIndex++) {
				for (int yIndex = yStart; yIndex <= yEnd; yIndex++) {
					for (int xIndex = xStart; xIndex <= xEnd; xIndex++) {
						int cellIndex = GetCellIndex(xIndex, yIndex, zIndex);
						if (sortedSpheres[cellIndex] == null) {
							continue;
						}
						for (int j = 0; j < sortedSpheres[cellIndex].Count; j++) {
							maxLoopCount++;
							int myIndex = i;
							int otherIndex = sortedSpheres[cellIndex][j];

							// 自分自身は対象外とする
							if (myIndex == otherIndex) {
								continue;
							}

							Vector3 diff = spherePositions[otherIndex] - spherePositions[myIndex];

							// 影響範囲内かの判定
							if (Mathf.Abs(diff.x) > SPHERE_FORCE_RADIUS) {
								continue;
							}
							if (Mathf.Abs(diff.y) > SPHERE_FORCE_RADIUS) {
								continue;
							}
							if (Mathf.Abs(diff.z) > SPHERE_FORCE_RADIUS) {
								continue;
							}
							if (diff.sqrMagnitude > SPHERE_FORCE_RADIUS * SPHERE_FORCE_RADIUS) {
								continue;
							}

							actualPairCount++;

							Vector3 normalized = diff.normalized;
							float magnitude = diff.magnitude;
							float standardizedMagnitude = magnitude / SPHERE_RADIUS; // 距離が半径の何倍かを表す数。各種加速度の計算に使用する。

							Vector3 acceleration = sphereAccelerations[myIndex];
							if (acceleration == null) {
								acceleration = Vector3.zero;
							}

							// 圧力をシミュレート(離れるように加速させる)
							if (standardizedMagnitude <= 1) {
								acceleration += -normalized * MAX_ACCELERATION_BY_PRESSURE * 1f;
							} else if (standardizedMagnitude < 2) {
								float pressureInfluence = 2 / (standardizedMagnitude - 1) + 2;
								if (pressureInfluence >= MAX_ACCELERATION_BY_PRESSURE) {
									acceleration += -normalized * MAX_ACCELERATION_BY_PRESSURE * 1f;
								} else {
									acceleration += -normalized * pressureInfluence * 1f;
								}
							}

							// 表面張力をシミュレート(離れないように加速させる)
							if (standardizedMagnitude > 2) {
								float surfaceTensionInfluence = 1 - (standardizedMagnitude - 3) * (standardizedMagnitude - 3);
								acceleration += normalized * surfaceTensionInfluence * 1f;
							}

							// 粘性をシミュレート(速度差を打ち消すように加速させる)
							float viscosityInfluence = (4f - standardizedMagnitude) / 4f;
							Vector3 velocityDiff = sphereVelocities[otherIndex] - sphereVelocities[myIndex];
							acceleration += velocityDiff * viscosityInfluence * 20f;

							sphereAccelerations[myIndex] = acceleration;
						}
					}
				}
			}
		});
		stopwatch.Stop();
		logText += "引力・斥力発生処理の時間       : " + stopwatch.Elapsed.TotalMilliseconds + " ms\n";
		logText += "引力・斥力発生候補のペア数     : " + maxLoopCount + " 個\n";
		logText += "引力・斥力発生が発生したペア数 : " + actualPairCount + " 個\n";

		// 加速の適用
		stopwatch.Restart();
		for (int i = 0; i < sphereCount; i++) {
			Vector3 acceleration = sphereAccelerations[i];
			if (acceleration == null) {
				continue;
			}
			sphereRigidbodies[i].WakeUp();
			sphereRigidbodies[i].AddForce(acceleration, ForceMode.Acceleration);
		}
		stopwatch.Stop();
		logText += "加速の適用処理の時間 : " + stopwatch.Elapsed.TotalMilliseconds + " ms\n";
	}

	private void OnDrawGizmosSelected() {
		for (int i = 0; i < sphereCount; i++) {
			if (sphereObjects[i] == null) {
				continue;
			}

			// 加速度の方向を描画
			Vector3 position = sphereObjects[i].transform.position;
			Vector3 acceleration = sphereAccelerations[i];
			if (acceleration == null) {
				continue;

			}
			Gizmos.color = Color.green;
			Gizmos.DrawLine(position, position + acceleration * 0.01f);

		}
	}

	private string logText = "";
	private void OnGUI() {
		string text = logText;

		// ログのテキストスタイルを設定
		GUIStyle guiStyleBack = new GUIStyle();
		guiStyleBack.fontSize = 20;
		guiStyleBack.normal.textColor = Color.black;
		GUIStyle guiStyleFront = new GUIStyle();
		guiStyleFront.fontSize = 20;
		guiStyleFront.normal.textColor = Color.white;

		// 画面上にログ出力
		GUI.Label(new Rect(12, 12, Screen.width, Screen.height), text, guiStyleBack);
		GUI.Label(new Rect(10, 10, Screen.width, Screen.height), text, guiStyleFront);
	}

	/// <summary>
	/// フルスクリーンエフェクト用メッシュを新規作成する。
	/// </summary>
	/// <returns>新規作成されたフルスクリーンエフェクト用メッシュ</returns>
	private Mesh CreateMeshForFullScreenEffect() {
		Vector3[] vertices = new Vector3[4];
		vertices[0] = new Vector3(-1, -1, 0);
		vertices[1] = new Vector3(1, -1, 0);
		vertices[2] = new Vector3(-1, 1, 0);
		vertices[3] = new Vector3(1, 1, 0);

		Vector2[] uv = new Vector2[4];
		uv[0] = new Vector2(0, 1);
		uv[1] = new Vector2(1, 1);
		uv[2] = new Vector2(0, 0);
		uv[3] = new Vector2(1, 0);

		int[] triangles = new int[] { 0, 1, 2, 2, 1, 3 };

		Mesh mesh = new Mesh();
		mesh.vertices = vertices;
		mesh.uv = uv;
		mesh.triangles = triangles;
		mesh.bounds = new Bounds(Vector3.zero, Vector3.one * float.MaxValue);
		mesh.RecalculateNormals();
		mesh.RecalculateTangents();

		return mesh;
	}

	void RespawnBall(int index) {
		Vector2 insideUnitCircle = Random.insideUnitCircle;

		Vector3 position = new Vector3(insideUnitCircle.x, Random.Range(10f, 50f), insideUnitCircle.y);
		Quaternion rotation = Random.rotation;

		sphereRigidbodies[index].position = position;
		sphereRigidbodies[index].rotation = rotation;
		sphereRigidbodies[index].mass = 4f / 3f * Mathf.PI * SPHERE_RADIUS * SPHERE_RADIUS * SPHERE_RADIUS * 1000f; // 水の密度を1000kg/(m^3)として計算
		sphereRigidbodies[index].linearDamping = 1.5f;  // FixedTime設定にもよるが、linearDamping = 1.5fの場合、終端速度は6m/sくらいになる
		sphereRigidbodies[index].linearVelocity = Vector3.down * 6f + Random.insideUnitSphere;
		sphereRigidbodies[index].angularVelocity = Vector3.zero;
		sphereRigidbodies[index].interpolation = RigidbodyInterpolation.Interpolate;
	}

	private int GetCellIndex(int x, int y, int z) {
		return z * GRID_DIVISION * GRID_DIVISION + y * GRID_DIVISION + x;
	}
}
