#ifndef METABALL_INCLUDED
#define METABALL_INCLUDED

/********/
/* 定数 */
/********/

#define MAX_SPHERE_COUNT 65536 // 球の最大数
#define MAX_STAKEHOLDERS_COUNT 64	// 単一Fragmentのレイマーチングの対象となる球の最大数


/******************************/
/* スクリプトから渡される情報 */
/******************************/

int _SphereCount; // 処理対象となる球の個数
float _SmoothWidth; // 滑らかなmin関数の滑らか度合い
float _MaxStakeholdableDistance; // 球をレイマーチング対象とする球表面⇔レイ間の最大の距離
int _MaxMarchCount; // レイの最大行進回数
float _MaxMarchDistance; // レイの最大行進距離(スクリプトから"カメラから最も遠い球の向こう側までの距離"を設定することを想定)
float _HitThreshold; // レイの衝突判定閾値(レイの先端とメタボールの距離がこの値以下になったら"ヒット"したと判定する)
int _DebugViewEnabled; // デバッグ表示(0:しない, 1:更新回数を表示)
StructuredBuffer<float4> _SpheresBuffer; // 球の位置情報(x:x座標, y:y座標, z:z座標, w:半径)


/**************************/
/* レイマーチング関連処理 */
/**************************/

// 滑らかなmin関数
float smoothMin(float a, float b, float w) {
	const float x = (b - a) / w;
	return x <= -1 ? b : x >= 1 ? a : b - w * 0.25 * (x + 1) * (x + 1);
}

// 球の距離関数
float sphereDistanceFunction(float4 sphere, float3 pos) {
	return length(sphere.xyz - pos) - sphere.w;
}

// 球との最短距離を返す
float getDistance(float3 pos, int stakeholdersIndices[MAX_STAKEHOLDERS_COUNT], int stakeholdersCount) {
	float distance = _MaxMarchDistance; // 初期値として十分に遠い距離を設定
	for (int i = 0; i < stakeholdersCount; i++) {
		distance = smoothMin(distance, sphereDistanceFunction(_SpheresBuffer[stakeholdersIndices[i]], pos), _SmoothWidth);
	}
	return distance;
}

// 指定位置の法線を偏微分により取得
float3 getNormal(const float3 pos, int stakeholdersIndices[MAX_STAKEHOLDERS_COUNT], int stakeholdersCount) {
	const float delta = _HitThreshold;
	const float x = getDistance(pos + float3(delta, 0, 0), stakeholdersIndices, stakeholdersCount) - getDistance(pos + float3(-delta, 0, 0), stakeholdersIndices, stakeholdersCount);
	const float y = getDistance(pos + float3(0, delta, 0), stakeholdersIndices, stakeholdersCount) - getDistance(pos + float3(0, -delta, 0), stakeholdersIndices, stakeholdersCount);
	const float z = getDistance(pos + float3(0, 0, delta), stakeholdersIndices, stakeholdersCount) - getDistance(pos + float3(0, 0, -delta), stakeholdersIndices, stakeholdersCount);
	return normalize(float3(x, y, z));
}


/****************************************/
/* Shader Graphからのエントリーポイント */
/****************************************/

void Metaball_float(
		float3 CameraPosition,
		float3 CameraDirection,
		float3 FragmentPosition,
		float SceneDepth,
		float4x4 ViewMatrix,
		out float3 BaseColor,
		out float Alpha,
		out float3 Normal,
		out float DepthOffset) {

	const float3 rayDir = normalize(FragmentPosition - CameraPosition); // レイの進行方向
	
	// 深度補正値の計算
	const float sceneDepthDistanceRate = 1 / dot(rayDir, CameraDirection);
	const float sceneDepthDistance = SceneDepth * sceneDepthDistanceRate;

	// デフォルト出力値の設定
	BaseColor = float3(1, 1, 1); // 色は白
	Alpha = 1; // アルファクリッピングは無し
	Normal = float3(0, 1, 0); // 法線は上向き
	DepthOffset = 0; // 深度オフセットは無し

	// このFragmentでレイマーチングの対象とする球(のindex)を事前にリストアップしておく
	int stakeholdersCount = 0; // このFragmentでレイマーチングの対象とする球の数
	int stakeholdersIndices[MAX_STAKEHOLDERS_COUNT]; // indexを格納する配列
	bool isStakeholdersOverflowed = false;
	for (int i = 0; i < _SphereCount; i++) {
		const float4 sphere = _SpheresBuffer[i];
		const float t = dot(rayDir, (sphere.xyz - CameraPosition)); // カメラ位置からレイと球の最近傍点までの長さ
		const float3 h = CameraPosition + rayDir * t; // レイと球の最近傍点の座標
		
		// レイと球の距離が_MaxStakeholdableDistance以下なら、球のindexを配列に格納する
		if (sphereDistanceFunction(sphere, h) <= _MaxStakeholdableDistance) {
			// 配列に入りきらなければエラーフラグを立てる
			if (MAX_STAKEHOLDERS_COUNT <= stakeholdersCount) {
				isStakeholdersOverflowed = true;
				break;
			}
			stakeholdersIndices[stakeholdersCount] = i;
			stakeholdersCount++;
		}
	}
	
	// 配列が空なら処理終了
	if (stakeholdersCount <= 0) {
		if (_DebugViewEnabled) {
			BaseColor = float3(0, 0, 0); // 黒一色を表示
		} else {
			Alpha = 0; // アルファを0にしてクリップさせる
		}
		return;
	}

	// レイマーチング準備
	float3 pos = CameraPosition; // レイの座標(カメラのワールド座標から開始)
	float totalDistance = 0; // 合計行進距離
	bool hit = false; // レイがヒットしたかどうかのフラグ、ヒットしなければfalseのまま
	int marchCount = 0;
	
	// レイマーチング開始
	for (marchCount = 1; marchCount <= _MaxMarchCount; marchCount++) {
		const float distance = getDistance(pos, stakeholdersIndices, stakeholdersCount);

		// 距離が閾値以下になったらヒットしたとみなして処理終了
		if (distance <= _HitThreshold) {
			hit = true;
			break;
		}

		// 合計行進距離が描画済み深度に到達したら強制終了
		if (totalDistance >= sceneDepthDistance) {
			if (_DebugViewEnabled) {
				const float marchRate = (float)marchCount / _MaxMarchCount;
				BaseColor = float3(0, 0, marchRate); // 青色で回数を表示
			} else {
				Alpha = 0; // アルファを0にしてクリップさせる
			}
			return;
		}

		// 合計行進距離が最大描画距離に到達したら強制終了
		if (totalDistance >= _MaxMarchDistance) {
			if (_DebugViewEnabled) {
				const float marchRate = (float)marchCount / _MaxMarchCount;
				BaseColor = float3(0, marchRate, marchRate); // 水色で回数を表示
			} else {
				Alpha = 0; // アルファを0にしてクリップさせる
			}
			return;
		}

		// レイの方向に行進
		pos += distance * rayDir;
		totalDistance += distance;
	}
	
	// 結果の表示
	if (_DebugViewEnabled) {
		const float marchRate = (float)marchCount / _MaxMarchCount;
		if (hit && !isStakeholdersOverflowed) {
			BaseColor = float3(0, marchRate, 0); // 正常にヒットした場合は緑色で回数表示
		} else {
			BaseColor = float3(marchRate, 0, 0); // 最後までヒットしなかった、あるいは配列があふれていた場合は赤色で回数表示
		}
	} else {
		if (hit) {
			// レイがヒットした場合
			Normal = getNormal(pos, stakeholdersIndices, stakeholdersCount);
			DepthOffset = (mul(ViewMatrix, float4(FragmentPosition.xyz, 1)).z - mul(ViewMatrix, float4(pos.xyz, 1)).z) * sceneDepthDistanceRate;
		} else {
			// レイがヒットしなかった場合
			Alpha = 0; // アルファを0にしてクリップさせる
		}
	}

	return;
}


#endif
